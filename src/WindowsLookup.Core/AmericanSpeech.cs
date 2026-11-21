using System.IO;

namespace WindowsLookup;

/// <summary>在本机用唯一音色 Amy 合成并播放单词。</summary>
public sealed class AmericanSpeech : IDisposable
{
    private const int PlaybackCacheLimit = 2 * 1024 * 1024;
    private readonly SemaphoreSlim _playbackGate = new(1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, Task<byte[]>> _inflightSynthesis = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<(string Word, byte[] Wave)>> _playbackCache =
        new(StringComparer.Ordinal);
    private readonly LinkedList<(string Word, byte[] Wave)> _playbackOrder = new();
    private readonly AmySpeechClient _client;
    private int _cachedBytes;

    /// <summary>
    /// 创建使用当前程序作为语音工作进程的朗读服务。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     AmericanSpeech - 在第一次使用时加载本地 Amy 的服务。
    /// </summary>
    public AmericanSpeech() : this(Environment.ProcessPath) { }

    /// <summary>
    /// 创建使用指定工作程序的朗读服务。
    ///
    /// Args:
    ///     executable - 支持 --speech-worker 的可执行文件路径。
    /// Returns:
    ///     AmericanSpeech - 复用同一语音进程和播放缓存的服务。
    /// </summary>
    internal AmericanSpeech(string? executable) => _client = new AmySpeechClient(executable);

    /// <summary>
    /// 用 Amy 离线合成英语单词。
    ///
    /// Args:
    ///     word - 需要朗读的不含空白的单词，最多 80 个字符。
    ///     cancellationToken - 取消本次请求，必要时重建已中断的连接。
    /// Returns:
    ///     WAV 字节 - PCM16 单声道音频，Amy 不可用时抛出语音异常。
    /// </summary>
    public Task<byte[]> SynthesizeAsync(string word, CancellationToken cancellationToken = default)
    {
        _validateWord(word);
        cancellationToken.ThrowIfCancellationRequested();
        return _client._synthesizeAsync(word, cancellationToken);
    }

    /// <summary>
    /// 用固定示例词在后台预热 Amy，失败时记录诊断。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值，模型可复用或后台预热完成后结束。
    /// </summary>
    public Task PrepareAsync() => _client._needsWarmup() ? Task.Run(_prepareAsync) : Task.CompletedTask;

    // 丢弃预热音频，异常由合成客户端记录，下次使用可重新尝试。
    private async Task _prepareAsync()
    {
        try
        {
            await _client._synthesizeAsync("test", CancellationToken.None, prewarm: true).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SpeechException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 后台合成当前单词并缓存音频，供正式朗读复用。
    ///
    /// Args:
    ///     word - 翻译流程规范化后的单词，最多 80 个字符且不含空白。
    ///     cancellationToken - 取消本次等待，其他调用方可继续使用共享合成。
    /// Returns:
    ///     无返回值，命中缓存或后台等待结束后完成；无效单词和语音故障时跳过。
    /// </summary>
    public async Task PrepareWordAsync(string word, CancellationToken cancellationToken = default)
    {
        if (!_isValidWord(word))
        {
            return;
        }
        try
        {
            await _getOrSynthesizeWaveAsync(word, cancellationToken, warmOnCacheHit: false).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SpeechException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 设置 Amy 进程的内存保留模式。
    ///
    /// Args:
    ///     mode - 动态续热或静态常驻。
    /// Returns:
    ///     无返回值，已有进程开始执行新的空闲规则。
    /// </summary>
    public void SetMemoryMode(SpeechMemoryMode mode) => _client._setMemoryMode(mode);

    /// <summary>
    /// 等待播放队列，并通过 Windows 当前默认设备朗读一个单词。
    ///
    /// Args:
    ///     word - 需要朗读的英文单词。
    ///     cancellationToken - 取消排队、等待合成或正在播放的音频。
    /// Returns:
    ///     无返回值，单词播放结束后任务完成。
    /// </summary>
    public async Task SpeakAsync(string word, CancellationToken cancellationToken = default)
    {
        await _playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _client._notePlayback();
            byte[] wave = await _getOrSynthesizeWaveAsync(word, cancellationToken).ConfigureAwait(false);
            await Win32WaveOutPlayer._playAsync(wave, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _client._notePlayback();
            _playbackGate.Release();
        }
    }

    // 缓存命中时更新访问顺序，未命中时合并同一个词的合成，各调用方独立等待。
    internal Task<byte[]> _getOrSynthesizeWaveAsync(string word, CancellationToken token, bool warmOnCacheHit = true)
    {
        _validateWord(word);
        token.ThrowIfCancellationRequested();
        Task<byte[]> synthesis;
        bool cached;
        lock (_cacheLock)
        {
            cached = _playbackCache.TryGetValue(word, out var node);
            if (node is not null)
            {
                _playbackOrder.Remove(node);
                _playbackOrder.AddLast(node);
                synthesis = Task.FromResult(node.Value.Wave);
            }
            else if (_inflightSynthesis.TryGetValue(word, out var pending) && !pending.IsFaulted && !pending.IsCanceled)
            {
                synthesis = pending;
            }
            else
            {
                synthesis = Task.Run(() => _synthesizeAndCacheAsync(word));
                _inflightSynthesis[word] = synthesis;
                _ = synthesis.ContinueWith(completed => _forgetSynthesis(word, completed),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        if (cached && warmOnCacheHit)
        {
            _ = PrepareAsync();
        }
        return synthesis.WaitAsync(token);
    }

    // 合成使用应用自己的生命周期，成功后按 2 MiB 上限写入缓存。
    private async Task<byte[]> _synthesizeAndCacheAsync(string word)
    {
        byte[] wave = await _client._synthesizeAsync(word, CancellationToken.None).ConfigureAwait(false);
        lock (_cacheLock)
        {
            if (wave.Length <= PlaybackCacheLimit && !_playbackCache.ContainsKey(word))
            {
                while (_cachedBytes > PlaybackCacheLimit - wave.Length)
                {
                    var oldest = _playbackOrder.First!;
                    _cachedBytes -= oldest.Value.Wave.Length;
                    _playbackCache.Remove(oldest.Value.Word);
                    _playbackOrder.RemoveFirst();
                }
                _playbackCache.Add(word, _playbackOrder.AddLast((word, wave)));
                _cachedBytes += wave.Length;
            }
        }
        return wave;
    }

    // 清掉已完成的同一任务，调用方都取消等待后也观察后台异常。
    private void _forgetSynthesis(string word, Task<byte[]> completed)
    {
        _ = completed.Exception;
        lock (_cacheLock)
        {
            if (_inflightSynthesis.TryGetValue(word, out var pending) && ReferenceEquals(pending, completed))
            {
                _inflightSynthesis.Remove(word);
            }
        }
    }

    // 合成和预合成采用相同的单词长度与空白规则。
    private static bool _isValidWord(string? word) =>
        !string.IsNullOrWhiteSpace(word) && word.Length <= 80 && !word.Any(char.IsWhiteSpace);

    // 保留正式合成对无效输入的参数异常。
    private static void _validateWord(string word)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        if (!_isValidWord(word))
        {
            throw new ArgumentException("朗读只接受最多 80 个字符的单词。", nameof(word));
        }
    }

    /// <summary>
    /// 运行从父应用收到请求的独立语音进程。
    ///
    /// Args:
    ///     pipeName - 父应用提供的当前用户命名管道。
    ///     parentPid - 父应用的进程编号。
    /// Returns:
    ///     退出码 - 正常关闭为 0，连接或初始化失败为非 0。
    /// </summary>
    public static int RunWorker(string pipeName, int parentPid) =>
        AmySpeechWorker._runAsync(pipeName, parentPid).GetAwaiter().GetResult();

    /// <summary>
    /// 启动真实 Amy 子进程并检查本地样例能够合成有效声音。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     退出码 - 成功为 0，合成、资源或音频校验失败为 1。
    /// </summary>
    public static int RunSelfTest()
    {
        try
        {
            using var speech = new AmericanSpeech(Environment.ProcessPath);
            byte[] wave = speech.SynthesizeAsync("test").GetAwaiter().GetResult();
            if (!SpeechWave._hasSound(wave))
            {
                throw new InvalidDataException("Amy 自检返回静音。");
            }
            Console.WriteLine("Amy 自检通过，已离线合成有效 PCM WAV。");
            return 0;
        }
        catch (Exception error)
        {
            if (error is SpeechException speech)
            {
                Console.Error.WriteLine($"Amy 自检失败：{speech.Kind}，{speech.Message} 代码 {speech.Code?.ToString() ?? "-"}。");
            }
            else
            {
                Console.Error.WriteLine($"Amy 自检失败：{error.GetType().Name}。");
            }
            return 1;
        }
    }

    /// <summary>
    /// 释放 Amy 的本地合成进程。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值。
    /// </summary>
    public void Dispose() => _client.Dispose();
}
