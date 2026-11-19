using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using Timer = System.Threading.Timer;
using System.Text;
using System.Text.Json;

namespace WindowsLookup;

/// <summary>在可重启的子进程内执行可能卡住的 Windows 选区读取。</summary>
internal sealed class SelectionAutomationWorker : IDisposable
{
    private static readonly TimeSpan ReadBudget = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan IdleTime = TimeSpan.FromMinutes(1);
    private readonly string _executable;
    private readonly object _sync = new();
    private readonly Timer _idleTimer;
    private Process? _process;
    private long _lastRead;
    private bool _reading;
    private bool _disposed;

    /// <summary>
    /// 创建按需启动的选区读取服务。
    ///
    /// Args:
    ///     executable - 支持内部选区读取模式的可执行文件。
    /// Returns:
    ///     SelectionAutomationWorker - 在首次取词时启动子进程。
    /// </summary>
    internal SelectionAutomationWorker(string executable)
    {
        _executable = executable;
        _idleTimer = new Timer(_releaseIdle);
    }

    // 一次只接收一份结果，超时或取消时结束进程，避免后续读取收到过期响应。
    internal async Task<SelectionCapture> _readAsync(nint sourceWindow, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ReadBudget);
        try
        {
            Process process;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _reading = true;
                process = _ensureProcess();
            }

            await process.StandardInput.WriteLineAsync(
                sourceWindow.ToInt64().ToString(CultureInfo.InvariantCulture).AsMemory(), deadline.Token)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            string? line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (line is null || JsonSerializer.Deserialize<SelectionReply>(line) is not { } reply)
            {
                throw new IOException("选区读取进程已停止。");
            }
            return new(reply.Status, reply.Text, reply.Detail) { SelectionBounds = reply.Bounds };
        }
        catch (OperationCanceledException)
        {
            lock (_sync) { _stopProcess(); }
            token.ThrowIfCancellationRequested();
            // 界面仍在原窗口时，由取词入口继续尝试安全的剪贴板路径。
            Trace.TraceWarning("选区接口响应超时，已停止读取子进程。");
            return new(CaptureStatus.Unavailable);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or JsonException)
        {
            lock (_sync) { _stopProcess(); }
            Trace.TraceWarning("选区读取进程中断（{0}）。", ex.GetType().Name);
            return new(CaptureStatus.Unavailable, Detail: "选区读取已中断，请再按 Alt+T 重试。");
        }
        finally
        {
            lock (_sync)
            {
                _reading = false;
                if (!_disposed && _process is not null)
                {
                    _lastRead = Stopwatch.GetTimestamp();
                    _idleTimer.Change(IdleTime, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    // 复用正常的读取进程，进程异常退出后按需重新启动。
    private Process _ensureProcess()
    {
        if (_process is { HasExited: false })
        {
            return _process;
        }
        _stopProcess();
        var start = new ProcessStartInfo(_executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--selection-worker");
        _process = Process.Start(start) ?? throw new IOException("无法启动选区读取进程。");
        return _process;
    }

    // 闲置时释放进程，定时任务与当前选区读取共用一把锁。
    private void _releaseIdle(object? state)
    {
        lock (_sync)
        {
            if (_disposed || _reading || _process is null)
            {
                return;
            }
            TimeSpan remaining = IdleTime - Stopwatch.GetElapsedTime(_lastRead);
            if (remaining <= TimeSpan.Zero)
            {
                _stopProcess();
            }
            else
            {
                _idleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
            }
        }
    }

    // 只能在持有进程锁时停止本实例的子进程。
    private void _stopProcess()
    {
        if (_process is not { } process)
        {
            return;
        }
        _process = null;
        using (process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Trace.TraceWarning("关闭选区读取进程失败（{0}）。", ex.GetType().Name);
            }
        }
    }

    // 子进程只读取父进程指定的前台窗口，通过标准输出逐行返回结果。
    internal static void _respond()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        while (Console.ReadLine() is { } line)
        {
            SelectionCapture capture = long.TryParse(line, CultureInfo.InvariantCulture, out long handle)
                && handle != 0 && (nint)handle == WindowsHotkey._getForegroundWindow()
                ? SelectionReader._readSelection((nint)handle)
                : new(CaptureStatus.Unavailable, Detail: "来源窗口已切换。");
            Console.WriteLine(JsonSerializer.Serialize(
                new SelectionReply(capture.Status, capture.Text, capture.Detail, capture.SelectionBounds)));
        }
    }

    /// <summary>
    /// 取消空闲计时并终止本实例的读取进程。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _idleTimer.Dispose();
            _stopProcess();
        }
    }

    private sealed record SelectionReply(CaptureStatus Status, string? Text, string? Detail, Rectangle? Bounds);
}
