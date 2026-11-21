using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsLookup;

/// <summary>通过 Windows 当前默认的 waveOut 设备播放内存中的 WAV。</summary>
internal static class Win32WaveOutPlayer
{
    private const uint WaveMapper = uint.MaxValue;
    private const uint CallbackEvent = 0x50000;
    private const uint HeaderDone = 1;
    private const uint StillPlaying = 33;
    private static readonly int FlagsOffset = Marshal.OffsetOf<Header>(nameof(Header.Flags)).ToInt32();
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<Header>();
    private static readonly TimeSpan PlaybackBudget = TimeSpan.FromSeconds(30);
    private static readonly Api Native = new(_open, _prepare, _write, _reset, _unprepare, _close);

    // 解析一次 WAV，设备打开或写入失败时重新打开当前默认设备再试一次。
    internal static async Task _playAsync(byte[] wave, CancellationToken token, Api? api = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        SpeechWave.Pcm pcm = SpeechWave._read(wave);
        api ??= Native;
        delay ??= Task.Delay;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await Task.Run(() => _playOnce(wave, pcm, api, token), token).ConfigureAwait(false);
                return;
            }
            catch (SpeechException error) when (attempt == 0 && error.Retryable && !token.IsCancellationRequested)
            {
                if (ReferenceEquals(api, Native))
                {
                    SpeechDiagnostics._record("waveout-retry", error.Kind, error.Code);
                }
                await delay(150, token).ConfigureAwait(false);
            }
        }
    }

    // 设备占用音频时保留头部和固定内存，取消后先停止设备再释放。
    private static void _playOnce(byte[] wave, SpeechWave.Pcm pcm, Api api, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var output = new Output(api);
        try
        {
            output._start(wave, pcm);
            long started = Stopwatch.GetTimestamp();
            WaitHandle[] handles = [output.DoneEvent, token.WaitHandle];
            while (!output._done())
            {
                token.ThrowIfCancellationRequested();
                TimeSpan remaining = PlaybackBudget - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero || WaitHandle.WaitAny(handles, remaining) == WaitHandle.WaitTimeout)
                {
                    throw _failure("waveOut playback timeout", 1460);
                }
            }
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            var failure = output._release();
            if (failure.Code == StillPlaying)
            {
                _ = output._retryReleaseAsync(failure);
            }
            else if (failure.Code != 0)
            {
                output._abandonRelease(failure);
            }
        }
    }

    // 把原生阶段和 MMRESULT 保留在结构化异常中供日志与托盘处理。
    private static SpeechException _failure(string operation, uint result, bool retryable = false) =>
        new(SpeechFailureKind.AudioDeviceFailed, operation, checked((int)result), retryable: retryable);

    private sealed class Output
    {
        private readonly Api _api;
        private nint _device;
        private nint _header;
        private GCHandle _bytes;
        private bool _prepared;
        private bool _submitted;
        internal EventWaitHandle DoneEvent { get; } = new(false, EventResetMode.AutoReset);

        /// <summary>
        /// 创建负责本次播放与资源释放的设备状态。
        ///
        /// Args:
        ///     api - 读写 waveOut 设备的原生调用集合。
        /// Returns:
        ///     Output - 打开设备前持有空闲完成事件的状态。
        /// </summary>
        internal Output(Api api) => _api = api;

        // 写入音频前清除设备打开时发出的 WOM_OPEN 事件。
        internal void _start(byte[] wave, SpeechWave.Pcm pcm)
        {
            var format = new Format
            {
                Tag = 1,
                Channels = pcm.Channels,
                Rate = (uint)pcm.Rate,
                BytesPerSecond = (uint)(pcm.Rate * pcm.Align),
                Align = pcm.Align,
                Bits = 16,
            };
            uint result = _api.Open(out nint device, WaveMapper, ref format, DoneEvent.SafeWaitHandle.DangerousGetHandle(),
                0, CallbackEvent);
            if (result != 0)
            {
                throw _failure("waveOutOpen", result, retryable: true);
            }

            _device = device;
            _bytes = GCHandle.Alloc(wave, GCHandleType.Pinned);
            _header = Marshal.AllocHGlobal((int)HeaderSize);
            Marshal.StructureToPtr(new Header
            {
                Data = _bytes.AddrOfPinnedObject() + pcm.Offset,
                Length = (uint)pcm.Length,
            }, _header, fDeleteOld: false);

            result = _api.Prepare(_device, _header, HeaderSize);
            if (result != 0)
            {
                throw _failure("waveOutPrepareHeader", result);
            }
            _prepared = true;
            DoneEvent.Reset();
            result = _api.Write(_device, _header, HeaderSize);
            if (result != 0)
            {
                throw _failure("waveOutWrite", result, retryable: true);
            }
            _submitted = true;
        }

        // 事件可能由设备打开或关闭发出，实际播放状态以头部标记为准。
        internal bool _done() => (Marshal.ReadInt32(_header, FlagsOffset) & HeaderDone) != 0;

        // 等待 Reset 置位 WHDR_DONE，再按驱动确认的顺序释放缓冲和事件。
        internal (string Stage, uint Code) _release()
        {
            if (_submitted && !_done())
            {
                uint result = _api.Reset(_device);
                if (result != 0)
                {
                    return ("waveout-reset", result);
                }
                if (!_done())
                {
                    DoneEvent.WaitOne(50);
                    if (!_done())
                    {
                        return ("waveout-reset", StillPlaying);
                    }
                }
            }
            if (_prepared)
            {
                uint result = _api.Unprepare(_device, _header, HeaderSize);
                if (result != 0)
                {
                    return ("waveout-unprepare", result);
                }
                _prepared = false;
                _submitted = false;
            }
            if (_header != 0)
            {
                Marshal.FreeHGlobal(_header);
                _header = 0;
            }
            if (_bytes.IsAllocated)
            {
                _bytes.Free();
            }
            if (_device != 0)
            {
                uint result = _api.Close(_device);
                if (result != 0)
                {
                    return ("waveout-close", result);
                }
                _device = 0;
            }
            DoneEvent.Dispose();
            return ("", 0);
        }

        // 只为暂态忙碌每 250 毫秒重试一次，最多重试 20 次。
        internal async Task _retryReleaseAsync((string Stage, uint Code) failure)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await timer.WaitForNextTickAsync().ConfigureAwait(false);
                failure = _release();
                if (failure.Code == 0)
                {
                    return;
                }
                if (failure.Code != StillPlaying)
                {
                    break;
                }
            }
            _abandonRelease(failure);
        }

        // 驱动仍可能持有缓冲或回调句柄，让未解绑资源留到进程退出并记录最终错误。
        internal void _abandonRelease((string Stage, uint Code) failure)
        {
            _ = GCHandle.Alloc(DoneEvent);
            if (_api.ReportCleanup is { } report)
            {
                report(failure.Stage, failure.Code);
            }
            else
            {
                SpeechDiagnostics._record(failure.Stage, SpeechFailureKind.AudioDeviceFailed, (int)failure.Code);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct Format
    {
        internal ushort Tag, Channels;
        internal uint Rate, BytesPerSecond;
        internal ushort Align, Bits, Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Header
    {
        internal nint Data;
        internal uint Length, Recorded;
        internal nuint User;
        internal uint Flags, Loops;
        internal nint Next;
        internal nuint Reserved;
    }

    // 打开指定默认设备并使用事件接收完成通知。
    internal delegate uint OpenDevice(out nint handle, uint id, ref Format format, nint callback, nuint instance, uint flags);

    // 保留真实调用顺序，同时允许验证设备异常和取消分支。
    internal sealed record Api(OpenDevice Open, Func<nint, nint, uint, uint> Prepare,
        Func<nint, nint, uint, uint> Write, Func<nint, uint> Reset, Func<nint, nint, uint, uint> Unprepare,
        Func<nint, uint> Close, Action<string, uint>? ReportCleanup = null);

    // 打开 Windows 的默认音频输出设备。
    [DllImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static extern uint _open(out nint handle, uint id, ref Format format, nint callback, nuint instance, uint flags);

    // 注册存有 PCM 数据的头部。
    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static extern uint _prepare(nint handle, nint header, uint size);

    // 将采样缓冲加入设备播放队列。
    [DllImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static extern uint _write(nint handle, nint header, uint size);

    // 中断播放并通知驱动释放排队缓冲。
    [DllImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static extern uint _reset(nint handle);

    // 撤销设备对已完成缓冲的引用。
    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static extern uint _unprepare(nint handle, nint header, uint size);

    // 完成播放后关闭设备。
    [DllImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static extern uint _close(nint handle);
}
