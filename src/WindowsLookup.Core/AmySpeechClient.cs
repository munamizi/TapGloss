using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Timer = System.Threading.Timer;

namespace WindowsLookup;

/// <summary>串行管理 Amy 子进程的启动、合成、自愈和空闲回收。</summary>
internal sealed class AmySpeechClient : IDisposable
{
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopBudget = TimeSpan.FromMilliseconds(500);
    private readonly string? _executable;
    private readonly string _directory;
    private readonly TimeSpan _idleTime;
    private readonly SemaphoreSlim _requests = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private readonly Timer _idleTimer;
    private WorkerConnection? _worker;
    private long _lastActivity;
    private bool _busy;
    private bool _primed;
    private bool _disposed;
    private SpeechMemoryMode _mode;

    /// <summary>
    /// 创建在首次使用时才启动 Amy 的客户端。
    ///
    /// Args:
    ///     executable - 支持语音工作模式的应用程序路径。
    ///     idleTime - 动态续热的等待时间，默认 15 分钟，可用于缩短回收测试。
    /// Returns:
    ///     AmySpeechClient - 持有独立语音进程的客户端。
    /// </summary>
    internal AmySpeechClient(string? executable, TimeSpan? idleTime = null)
    {
        _executable = executable;
        _directory = !string.IsNullOrEmpty(executable)
            ? Path.GetDirectoryName(Path.GetFullPath(executable)) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
        _idleTime = idleTime ?? TimeSpan.FromMinutes(15);
        _idleTimer = new Timer(_onIdle);
    }

    /// <summary>
    /// 检查当前应用目录中的 Amy 模型数据和原生库。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     缺失文件的相对路径，完整时返回 null。
    /// </summary>
    internal static string? ValidateVoiceBundle() => AmyVoiceBundle._findMissing(AppContext.BaseDirectory);

    // 串行合成，界面取消时丢弃已读完的音频，应用退出时停止子进程。
    internal async Task<byte[]> _synthesizeAsync(string word, CancellationToken cancellationToken, bool prewarm = false)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken token = request.Token;
        await _requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (prewarm && _primed && _worker is { Process.HasExited: false })
                {
                    return [];
                }
                _busy = true;
            }

            byte[] wave = await _withRetryAsync(() => _synthesizeOnceAsync(word, token), async error =>
            {
                SpeechDiagnostics._record("worker-recycle", error.Kind, error.Code, _worker?._diagnostics());
                await _stopWorkerAsync(graceful: false).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            lock (_sync)
            {
                _primed = true;
            }
            token.ThrowIfCancellationRequested();
            return wave;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            await _stopWorkerAsync(graceful: false).ConfigureAwait(false);
            throw;
        }
        catch (SpeechException error)
        {
            SpeechDiagnostics._record("synthesis", error.Kind, error.Code, _worker?._diagnostics());
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _busy = false;
                if (!_disposed && _worker is not null)
                {
                    _scheduleIdle();
                }
            }
            _requests.Release();
        }
    }

    // 对断管、异常退出和超时先清理连接，随后最多再试一次。
    internal static async Task<T> _withRetryAsync<T>(Func<Task<T>> action,
        Func<SpeechException, Task> recycle, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (SpeechException error) when (error.Retryable)
            {
                await recycle(error).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (attempt == 1)
                {
                    throw;
                }
            }
        }
    }

    // 启动完成后提交整次管道请求，只让应用退出或合成超时中断传输。
    private async Task<byte[]> _synthesizeOnceAsync(string word, CancellationToken token)
    {
        WorkerConnection worker;
        try
        {
            worker = await _ensureWorkerAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await _stopWorkerAsync(graceful: false).ConfigureAwait(false);
            throw;
        }

        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(RequestBudget);
        try
        {
            await AmySpeechWorker._writeRequestAsync(worker.Pipe, word, deadline.Token).ConfigureAwait(false);
            var (status, value) = await AmySpeechWorker._readHeaderAsync(worker.Pipe, deadline.Token).ConfigureAwait(false);
            if (status == 1)
            {
                var (_, code) = await AmySpeechWorker._readErrorAsync(worker.Pipe, value, deadline.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                throw new SpeechException(SpeechFailureKind.SynthesisFailed, "Amy 合成失败。", code);
            }

            byte[] wave = await AmySpeechWorker._readBytesAsync(worker.Pipe, value, deadline.Token).ConfigureAwait(false);
            if (SpeechWave._read(wave).Rate != worker.SampleRate)
            {
                throw new InvalidDataException("Amy 返回的采样率与握手不符。");
            }
            return wave;
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            throw new SpeechException(SpeechFailureKind.SynthesisTimeout, "Amy 合成超时。", error.HResult, error, retryable: true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or DecoderFallbackException)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            throw new SpeechException(SpeechFailureKind.SynthesisFailed, "Amy 子进程连接中断。",
                worker._exitCode() ?? error.HResult, error, retryable: true);
        }
    }

    // 从应用目录启动单一子进程，标准输出单独排空，音频只走当前用户的命名管道。
    private async Task<WorkerConnection> _ensureWorkerAsync(CancellationToken token)
    {
        if (_worker is { Process.HasExited: false, Pipe.IsConnected: true } alive)
        {
            return alive;
        }

        await _stopWorkerAsync(graceful: false).ConfigureAwait(false);
        AmyVoiceBundle._requireComplete(_directory);
        if (string.IsNullOrEmpty(_executable) || !File.Exists(_executable))
        {
            throw new SpeechException(SpeechFailureKind.WorkerStartFailed, "找不到 Amy 的宿主程序。");
        }

        string name = $"TapGloss.Speech.{Environment.ProcessId}.{Guid.NewGuid():N}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(StartupBudget);
        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("--speech-worker");
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        var worker = new WorkerConnection(name, start);
        lock (_sync)
        {
            _worker = worker;
        }

        try
        {
            if (!worker.Process.Start())
            {
                throw new InvalidOperationException("未能启动 Amy 子进程。");
            }
            ChildProcessJob._assign(worker.Process);
            worker.Process.BeginErrorReadLine();
            worker.Process.BeginOutputReadLine();
            await worker.Pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            var (status, value) = await AmySpeechWorker._readHeaderAsync(worker.Pipe, deadline.Token).ConfigureAwait(false);
            if (status == 1)
            {
                var (kind, code) = await AmySpeechWorker._readErrorAsync(worker.Pipe, value, deadline.Token).ConfigureAwait(false);
                if (kind == SpeechFailureKind.BundleIncomplete)
                {
                    await _stopWorkerAsync(graceful: false).ConfigureAwait(false);
                    throw new SpeechException(kind, "Amy 的模型数据或本机库无法加载。", code);
                }
                throw new SpeechException(SpeechFailureKind.WorkerStartFailed, "Amy 初始化失败。", code, retryable: true);
            }
            if (value is < 8000 or > 192000)
            {
                throw new InvalidDataException("Amy 握手采样率无效。");
            }
            worker.SampleRate = value;
            return worker;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new SpeechException(SpeechFailureKind.WorkerStartFailed, "Amy 启动超时。",
                worker._exitCode() ?? error.HResult, error, retryable: true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or Win32Exception
            or DecoderFallbackException)
        {
            token.ThrowIfCancellationRequested();
            int code = worker._exitCode() ?? (error is Win32Exception win32 ? win32.NativeErrorCode : error.HResult);
            throw new SpeechException(SpeechFailureKind.WorkerStartFailed, "Amy 子进程未就绪。", code, error, retryable: true);
        }
    }

    // 查询当前合成子进程，供生命周期验收核对退出和重启。
    internal int? _workerPid()
    {
        lock (_sync)
        {
            return _primed && _worker is { Process.HasExited: false } ? _worker.Process.Id : null;
        }
    }

    // 已有可复用的模型时，不再排队预热。
    internal bool _needsWarmup()
    {
        lock (_sync)
        {
            return !_disposed && !_busy && (!_primed || _worker is not { Process.HasExited: false });
        }
    }

    // 切换保留模式，动态续热从当前时刻重新开始计时。
    internal void _setMemoryMode(SpeechMemoryMode mode)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mode != mode)
            {
                _mode = mode;
                _scheduleIdle();
            }
        }
    }

    // 播放缓存音频也算一次使用，延后仍在内存中的合成进程回收。
    internal void _notePlayback()
    {
        lock (_sync)
        {
            if (!_disposed && _worker is not null)
            {
                _scheduleIdle();
            }
        }
    }

    // 调用方持有状态锁时更新时间并控制单次空闲计时器。
    private void _scheduleIdle()
    {
        _lastActivity = Stopwatch.GetTimestamp();
        _idleTimer.Change(_mode == SpeechMemoryMode.DynamicWarmth ? _idleTime : Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    // 计时器回调启动可观察的回收流程，与合成请求串行。
    private void _onIdle(object? _) => _ = _releaseIdleAsync();

    // 二次核对模式和最后活动时间，过期的计时回调不会提前退出 worker。
    private async Task _releaseIdleAsync()
    {
        await _requests.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_disposed || _worker is null || _mode != SpeechMemoryMode.DynamicWarmth)
                {
                    return;
                }
                TimeSpan remaining = _idleTime - Stopwatch.GetElapsedTime(_lastActivity);
                if (remaining > TimeSpan.Zero)
                {
                    _idleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                    return;
                }
            }
            await _stopWorkerAsync(graceful: true).ConfigureAwait(false);
        }
        finally
        {
            _requests.Release();
        }
    }

    // 解绑旧连接，优雅关闭最多等 500 毫秒，故障或未退出时终止进程。
    private async Task _stopWorkerAsync(bool graceful)
    {
        WorkerConnection? worker;
        lock (_sync)
        {
            worker = _worker;
            _worker = null;
            _primed = false;
        }
        if (worker is null)
        {
            return;
        }

        using (worker.Pipe)
        using (worker.Process)
        {
            try
            {
                if (graceful && worker.Pipe.IsConnected && !worker.Process.HasExited)
                {
                    using var deadline = new CancellationTokenSource(StopBudget);
                    try
                    {
                        await AmySpeechWorker._writeRequestAsync(worker.Pipe, null, deadline.Token).ConfigureAwait(false);
                        await worker.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException)
                    {
                        SpeechDiagnostics._record("worker-shutdown", SpeechFailureKind.WorkerStartFailed, error.HResult);
                    }
                }
                if (!worker.Process.HasExited)
                {
                    worker.Process.Kill();
                    using var deadline = new CancellationTokenSource(StopBudget);
                    await worker.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or OperationCanceledException)
            {
                SpeechDiagnostics._record("worker-stop", SpeechFailureKind.WorkerStartFailed, error.HResult);
            }
        }
    }

    /// <summary>
    /// 取消待处理合成并停止计时器和本客户端的语音进程。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值，进程停止后完成。
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
        }
        _shutdown.Cancel();
        _requests.Wait();
        try
        {
            _stopWorkerAsync(graceful: true).GetAwaiter().GetResult();
        }
        finally
        {
            _requests.Release();
        }
    }

    private sealed class WorkerConnection
    {
        private readonly Queue<string> _stderr = new(32);
        internal NamedPipeServerStream Pipe { get; }
        internal Process Process { get; }
        internal int SampleRate { get; set; }

        /// <summary>
        /// 为一次 Amy 子进程启动创建管道和脱敏诊断缓冲区。
        ///
        /// Args:
        ///     pipeName - 本次启动的唯一管道名。
        ///     start - 子进程启动参数，标准输出和错误输出单独重定向。
        /// Returns:
        ///     WorkerConnection - 持有管道和待启动进程的连接对象。
        /// </summary>
        internal WorkerConnection(string pipeName, ProcessStartInfo start)
        {
            Pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Process = new Process { StartInfo = start };
            Process.ErrorDataReceived += (_, data) =>
            {
                if (!string.IsNullOrWhiteSpace(data.Data))
                {
                    lock (_stderr)
                    {
                        if (_stderr.Count == 32)
                        {
                            _stderr.Dequeue();
                        }
                        _stderr.Enqueue(SpeechDiagnostics._classifyNativeLine(data.Data));
                    }
                }
            };
        }

        // 返回最近原生输出的分类副本，避免与后台写入并发枚举。
        internal string[] _diagnostics()
        {
            lock (_stderr)
            {
                return _stderr.ToArray();
            }
        }

        // 只在进程已退出时提供实际退出码。
        internal int? _exitCode()
        {
            try { return Process.HasExited ? Process.ExitCode : null; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
