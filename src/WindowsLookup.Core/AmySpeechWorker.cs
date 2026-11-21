using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using SherpaOnnx;

namespace WindowsLookup;

/// <summary>在独立进程加载 Amy 并通过命名管道返回音频。</summary>
internal static class AmySpeechWorker
{
    internal const int MaxTextBytes = 320;
    internal const int MaxErrorBytes = 512;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // 在父进程退出时立即终止，避免原生推理或加载卡住后继续占用资源。
    internal static async Task<int> _runAsync(string pipeName, int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.Exited += (_, _) => Environment.Exit(0);
            parent.EnableRaisingEvents = true;
            if (parent.HasExited)
            {
                return 0;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            }

            OfflineTts tts;
            int sampleRate;
            try
            {
                tts = _loadModel();
                sampleRate = tts.SampleRate;
            }
            catch (Exception error)
            {
                var kind = error is SpeechException speech ? speech.Kind
                    : error is DllNotFoundException or BadImageFormatException ? SpeechFailureKind.BundleIncomplete
                    : SpeechFailureKind.WorkerStartFailed;
                await _writeErrorAsync(pipe, kind, error.HResult).ConfigureAwait(false);
                return 1;
            }

            using (tts)
            {
                await _writeHeaderAsync(pipe, 0, sampleRate).ConfigureAwait(false);
                var sizeBytes = new byte[sizeof(int)];
                while (true)
                {
                    await pipe.ReadExactlyAsync(sizeBytes).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
                    if (length == 0)
                    {
                        return 0;
                    }
                    if (length is < 0 or > MaxTextBytes)
                    {
                        return 2;
                    }
                    string word = Utf8.GetString(await _readBytesAsync(pipe, length, CancellationToken.None).ConfigureAwait(false));
                    byte[] wave;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(word) || word.Length > 80 || word.Any(char.IsWhiteSpace))
                        {
                            throw new InvalidDataException("请求只接受单个单词。");
                        }
                        OfflineTtsGeneratedAudio audio = tts.Generate(text: word, speed: 1.0f, speakerId: 0);
                        wave = SpeechWave._encode(audio.Samples, audio.SampleRate);
                        if (!SpeechWave._hasSound(wave))
                        {
                            throw new InvalidDataException("Amy 返回了静音。");
                        }
                    }
                    catch (Exception error)
                    {
                        await _writeErrorAsync(pipe, SpeechFailureKind.SynthesisFailed, error.HResult).ConfigureAwait(false);
                        continue;
                    }
                    await _writeHeaderAsync(pipe, 0, wave.Length).ConfigureAwait(false);
                    await pipe.WriteAsync(wave).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ArgumentException)
        {
            return error is EndOfStreamException or ArgumentException ? 0 : 1;
        }
    }

    // 只在子进程中切换工作目录，给本机库传 ASCII 相对路径。
    private static OfflineTts _loadModel()
    {
        string root = AppContext.BaseDirectory;
        AmyVoiceBundle._requireComplete(root);
        Directory.SetCurrentDirectory(Path.Combine(root, "voice"));
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = AmyVoiceBundle.Model;
        config.Model.Vits.Tokens = AmyVoiceBundle.Tokens;
        config.Model.Vits.DataDir = AmyVoiceBundle.EspeakData;
        config.Model.Vits.NoiseScale = 0.667f;
        config.Model.Vits.NoiseScaleW = 0.8f;
        config.Model.Vits.LengthScale = 1.0f;
        config.Model.NumThreads = 2;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        return new OfflineTts(config);
    }

    // 状态与长度或采样率用固定的五字节小端帧头传输。
    private static ValueTask _writeHeaderAsync(Stream pipe, byte status, int value)
    {
        var header = new byte[5];
        header[0] = status;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), value);
        return pipe.WriteAsync(header.AsMemory());
    }

    // 只在错误帧中发送分类和错误码，避免泄露请求文字与本地路径。
    private static async Task _writeErrorAsync(Stream pipe, SpeechFailureKind kind, int code)
    {
        byte[] payload = Utf8.GetBytes(FormattableString.Invariant($"{kind}:{code}"));
        await _writeHeaderAsync(pipe, 1, payload.Length).ConfigureAwait(false);
        await pipe.WriteAsync(payload).ConfigureAwait(false);
    }

    // 父进程发送长度和单词，空内容表示关闭。
    internal static ValueTask _writeRequestAsync(Stream pipe, string? word, CancellationToken token)
    {
        int size = word is null ? 0 : Utf8.GetByteCount(word);
        var frame = new byte[sizeof(int) + size];
        BinaryPrimitives.WriteInt32LittleEndian(frame, size);
        if (word is not null)
        {
            Utf8.GetBytes(word, frame.AsSpan(sizeof(int)));
        }
        return pipe.WriteAsync(frame.AsMemory(), token);
    }

    // 父进程完整读帧头，拒绝协议之外的状态和长度。
    internal static async Task<(byte Status, int Value)> _readHeaderAsync(Stream pipe, CancellationToken token)
    {
        var header = new byte[5];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int value = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (header[0] > 1 || value <= 0 || header[0] == 1 && value > MaxErrorBytes)
        {
            throw new InvalidDataException("Amy 返回了无效的协议帧。");
        }
        return (header[0], value);
    }

    // 按协议长度读取有界数据，断管由读取异常报告。
    internal static async Task<byte[]> _readBytesAsync(Stream pipe, int length, CancellationToken token)
    {
        if (length <= 0 || length > SpeechWave.MaxBytes)
        {
            throw new InvalidDataException("Amy 返回的数据长度超出范围。");
        }
        var data = new byte[length];
        await pipe.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return data;
    }

    // 将子进程的安全错误帧还原为诊断类别与数值，屏蔽额外文本。
    internal static async Task<(SpeechFailureKind Kind, int? Code)> _readErrorAsync(Stream pipe, int length, CancellationToken token)
    {
        byte[] payload = await _readBytesAsync(pipe, length, token).ConfigureAwait(false);
        string message = Utf8.GetString(payload);
        int colon = message.IndexOf(':');
        if (colon <= 0 || !Enum.TryParse(message.AsSpan(0, colon), out SpeechFailureKind kind) || !Enum.IsDefined(kind))
        {
            throw new InvalidDataException("Amy 返回了无效的错误帧。");
        }
        return (kind, int.TryParse(message.AsSpan(colon + 1), out int code) ? code : null);
    }
}
