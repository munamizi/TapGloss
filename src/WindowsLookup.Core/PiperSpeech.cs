using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Timer = System.Threading.Timer;

namespace WindowsLookup;

/// <summary>通过应用自带的 Piper 进程离线合成 Amy，并在空闲时释放模型。</summary>
internal sealed class PiperSpeech : IDisposable
{
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(3);
    private const int IdleMilliseconds = 15 * 60 * 1000;
    private readonly SemaphoreSlim _requests = new(1);
    private readonly object _sync = new();
    private readonly Timer _idleTimer;
    private Process? _worker;
    private bool _busy;
    private bool _disposed;
    private bool _primed;
    private long _lastActivity;
    private SpeechMemoryMode _memoryMode;

    /// <summary>
    /// 准备 Piper 的进程和空闲释放管理。
    /// Args:
    ///     无。
    /// Returns:
    ///     PiperSpeech - 预加载或合成时才启动 Piper 的实例。
    /// </summary>
    internal PiperSpeech() => _idleTimer = new Timer(_releaseIdleWorker);

    // 串行读写合成请求，取消播放时仍收完当前响应，保留可复用的进程。
    internal async Task<byte[]> _synthesizeAsync(string word, CancellationToken cancellationToken, bool prewarmOnly = false)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? wave = null;
        bool waitingForReady = false;
        bool requestStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process worker;
            bool starting;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (prewarmOnly && _primed && _worker is { HasExited: false })
                {
                    return [];
                }
                _busy = true;
                worker = _getOrStartWorker(out starting);
            }

            if (starting)
            {
                waitingForReady = true;
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(StartupBudget);
                await _readProtocolAsync(worker, waitingForReady: true, startup.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                waitingForReady = false;
            }

            // 请求发出前可以直接取消，发出后要读完响应才能安全复用进程。
            cancellationToken.ThrowIfCancellationRequested();
            requestStarted = true;
            using var deadline = new CancellationTokenSource(RequestBudget);
            await worker.StandardInput.WriteLineAsync(word.AsMemory(), deadline.Token).ConfigureAwait(false);
            await worker.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            string reply = await _readProtocolAsync(worker, waitingForReady: false, deadline.Token).ConfigureAwait(false);
            if (reply != "ERR")
            {
                wave = Convert.FromBase64String(reply[4..]);
                if (wave.Length <= 44 || !wave.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                    || !wave.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                {
                    throw new InvalidDataException("Piper 未返回有效的一段 WAV。");
                }
                lock (_sync)
                {
                    if (ReferenceEquals(_worker, worker))
                    {
                        _primed = true;
                    }
                }
            }
        }
        catch (OperationCanceledException ex) when (waitingForReady || requestStarted)
        {
            lock (_sync)
            {
                _stopWorker();
            }
            if (waitingForReady)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new TimeoutException("Amy 发音超时，请重试。", ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or Win32Exception or InvalidOperationException)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _stopWorker();
            }
            if (waitingForReady)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (ex is FileNotFoundException)
            {
                throw;
            }
            throw new InvalidOperationException("Amy 本地合成失败。", ex);
        }
        finally
        {
            lock (_sync)
            {
                if (_busy)
                {
                    _busy = false;
                    if (!_disposed && _worker is not null)
                    {
                        _scheduleRelease();
                    }
                }
            }
            _requests.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return wave ?? throw new InvalidOperationException("Amy 无法合成本次单词。");
    }

    // 忽略第三方库偶尔写入 stdout 的杂项行，EOF 和超时仍由调用方清理进程。
    private static async Task<string> _readProtocolAsync(Process worker, bool waitingForReady, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string line = await worker.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)
                ?? throw new IOException("Amy 语音进程已停止。");
            if (waitingForReady ? line == "READY" : line == "ERR" || line.StartsWith("WAV:", StringComparison.Ordinal))
            {
                return line;
            }
        }
    }

    // 判断是否需要在后台重新预热，实际启动由合成队列串行处理。
    internal bool _needsWarmup()
    {
        lock (_sync)
        {
            return !_disposed && !_busy && (!_primed || _worker is not { HasExited: false });
        }
    }

    // 切换到动态续热时，从切换时起重新计时。
    internal void _setMemoryMode(SpeechMemoryMode mode)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_memoryMode == mode)
            {
                return;
            }
            _memoryMode = mode;
            if (_worker is not null)
            {
                _scheduleRelease();
            }
        }
    }

    // 播放缓存音频也算一次使用，更新仍在内存中的进程。
    internal void _notePlayback()
    {
        lock (_sync)
        {
            if (!_disposed && _worker is not null)
            {
                _scheduleRelease();
            }
        }
    }

    // 调用方持有 _sync 时，从发布目录启动私有解释器或复用现有进程。
    private Process _getOrStartWorker(out bool started)
    {
        if (_worker is { HasExited: false } active)
        {
            started = false;
            return active;
        }
        started = true;
        _stopWorker();

        string voice = Path.Combine(AppContext.BaseDirectory, "voice");
        string python = Path.Combine(voice, "python", "python.exe");
        string script = Path.Combine(voice, "worker.py");
        string model = Path.Combine(voice, "en_US-amy-medium.onnx");
        if (!File.Exists(python) || !File.Exists(script) || !File.Exists(model) || !File.Exists(model + ".json"))
        {
            throw new FileNotFoundException("Amy 语音文件不完整。");
        }
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = voice,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.ASCII,
        };
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("utf8");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(script);
        _worker = Process.Start(start) ?? throw new IOException("无法启动 Piper。");
        // 持续排空日志管道，避免第三方库输出阻塞合成。
        _worker.BeginErrorReadLine();
        return _worker;
    }

    // 记录最近使用时间，只有动态续热才启动空闲计时器。
    private void _scheduleRelease()
    {
        _lastActivity = Environment.TickCount64;
        int delay = _memoryMode == SpeechMemoryMode.DynamicWarmth ? IdleMilliseconds : Timeout.Infinite;
        _idleTimer.Change(delay, Timeout.Infinite);
    }

    // 回调再次核对模式和最近使用时间，避免已经排队的旧回调提前释放。
    private void _releaseIdleWorker(object? _)
    {
        lock (_sync)
        {
            if (_disposed || _busy || _worker is null || _memoryMode != SpeechMemoryMode.DynamicWarmth)
            {
                return;
            }

            long remaining = IdleMilliseconds - (Environment.TickCount64 - _lastActivity);
            if (remaining <= 0)
            {
                _stopWorker();
            }
            else
            {
                _idleTimer.Change(remaining, Timeout.Infinite);
            }
        }
    }

    // 调用方持有 _sync 时，停止本实例启动的进程并清除句柄。
    private void _stopWorker()
    {
        if (_worker is not { } worker)
        {
            return;
        }
        _worker = null;
        _primed = false;
        try
        {
            if (!worker.HasExited)
            {
                worker.Kill();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Piper 进程结束时返回 {0}。", ex.GetType().Name);
        }
        finally
        {
            worker.Dispose();
        }
    }

    /// <summary>
    /// 停止空闲计时和私有语音进程。
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值，重复释放安全。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _idleTimer.Dispose();
            _stopWorker();
        }
    }
}

