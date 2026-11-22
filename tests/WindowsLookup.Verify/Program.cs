using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WindowsLookup;

namespace WindowsLookup.Verify;

internal static class Program
{
    /// <summary>
    /// 运行 TapGloss 单元测试，或用发布程序验证原生音频与进程生命周期。
    ///
    /// Args:
    ///     args - 验证模式及其临时目录、发布程序路径或测试管道参数。
    /// Returns:
    ///     退出码 - 验证通过为 0，测试断言失败为 1，参数无效为 2。
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["--unit", string directory]:
                    string fixture = _copyWorkerFixture(_testBundle(directory));
                    _testWordNormalization();
                    _testSelectionRules();
                    _testNativeWindows();
                    _testDpapi(directory);
                    await _testJobAsync();
                    byte[] wave = _testWave();
                    await _testRetryAsync();
                    await _testPlaybackAsync(wave);
                    await _testAsyncResetAsync(wave);
                    await _testCleanupFailuresAsync(wave);
                    await _testRequestCancellationAsync(fixture);
                    await _testWordPreparationAsync(fixture);
                    await _testSelectionLifetimeAsync(fixture);
                    _testDiagnostics(directory);
                    Console.WriteLine("TapGloss 单元测试通过，含 Job、UIA、DPAPI、原生窗口和语音回归。");
                    return 0;
                case ["--integration", string application]:
                    await _testWorkerAsync(Path.GetFullPath(application));
                    return 0;
                case ["--waveout", string application]:
                    using (var speech = new AmericanSpeech(Path.GetFullPath(application)))
                    {
                        await speech.PrepareWordAsync("test");
                        await speech.SpeakAsync("test");
                    }
                    Console.WriteLine("Windows 默认输出设备的 waveOut 播放已结束。");
                    return 0;
                case ["--amy-parent", string application]:
                    using (var client = new AmySpeechClient(Path.GetFullPath(application)))
                    {
                        await client._synthesizeAsync("test", CancellationToken.None);
                        Console.WriteLine(client._workerPid());
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                    }
                    return 0;
                case ["--speech-worker", string pipeName, string parent] when int.TryParse(parent, out int parentPid):
                    await _runPipeFixtureAsync(pipeName, parentPid);
                    return 0;
                case ["--job-parent"]:
                    await _runJobParentAsync();
                    return 0;
                case ["--job-child"]:
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                    return 0;
                case ["--selection-worker"]:
                    _runSelectionFixture();
                    return 0;
                default:
                    return 2;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    // 验证扁平矩形、密码拦截、终端祖先补查及焦点变化的规则。
    private static void _testSelectionRules()
    {
        _check(SelectionReader._lastVisibleBounds([10.5, 11.2, 40, 15]) == Rectangle.FromLTRB(10, 11, 51, 27),
            "单行选区应按物理像素向外取整。");
        _check(SelectionReader._lastVisibleBounds([1, 2, 3, 4, 8, 9, 10, 11, double.NaN, 1, 2, 3, 4, 5, -1, 6])
            == new Rectangle(8, 9, 10, 11), "倒序跳过 NaN 和负宽度，使用最后的有效可见行。");
        _check(SelectionReader._lastVisibleBounds([1, 2, 3, 4, 5]) is null
            && SelectionReader._lastVisibleBounds([1, 2, 3, double.PositiveInfinity]) is null,
            "不完整矩形和无穷边界应忽略。");

        SelectionCapture terminal = SelectionReader._scanFocusChain("edit", _ => "xterm",
            (node, _) => node == "xterm",
            (node, _) => node == "edit" ? new(CaptureStatus.Selected, "sample") : null, () => true);
        _check(terminal is { Status: CaptureStatus.Selected, IsTerminal: true },
            "成功获取文本后仍要检查终端祖先。");
        SelectionCapture empty = SelectionReader._scanFocusChain("edit", _ => "xterm",
            (node, _) => node == "xterm", (_, _) => new(CaptureStatus.NoSelection), () => true);
        _check(empty is { Status: CaptureStatus.Unavailable, FallbackMode: ClipboardFallbackMode.None },
            "终端空选区不得转到自动剪贴板。");
        SelectionCapture password = SelectionReader._scanFocusChain("edit", _ => null, (_, _) => false,
            (_, _) => new(CaptureStatus.Protected), () => true);
        _check(password is { Status: CaptureStatus.Protected, FallbackMode: ClipboardFallbackMode.None },
            "密码控件应立即停止取词。");
        SelectionCapture moved = SelectionReader._scanFocusChain("edit", _ => null, (_, _) => false,
            (_, _) => new(CaptureStatus.Selected, "sample"), () => false);
        _check(moved is { Status: CaptureStatus.Unavailable, Text: null },
            "焦点更改后不能返回旧控件的文字。");
        SelectionCapture tooLong = SelectionReader._scanFocusChain("edit", _ => null, (_, _) => false,
            (_, _) => new(CaptureStatus.TooLong), () => true);
        _check(tooLong is { Status: CaptureStatus.TooLong, FallbackMode: ClipboardFallbackMode.None },
            "超出长度限制后不能转到自动剪贴板。");
    }

    // 用同一个文件验证旧 DPAPI 密文和当前存储的往返兼容。
    private static void _testDpapi(string directory)
    {
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".key");
        const string fixture = "tapgloss-dpapi-fixture";
        var store = new DeepSeekKeyStore(path);
        try
        {
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(fixture), null, DataProtectionScope.CurrentUser));
            _check(store.Load() == fixture, "应能读取旧版 CurrentUser DPAPI 密文。");
            store.Save(" " + fixture + " ");
            _check(store.Load() == fixture
                && Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)) == fixture,
                "新密文应可往返，并可由旧版规则解密。");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 直接向测试窗口发送消息，并验证窗口、注册键和图标句柄可以释放。
    private static void _testNativeWindows()
    {
        const uint verifyMessage = 0x8051;
        foreach (bool messageOnly in new[] { true, false })
        {
            bool called = false;
            using var window = new Win32MessageWindow(messageOnly, (message, wParam, lParam) =>
            {
                called = message == verifyMessage && wParam == 6 && lParam == 9;
                return called;
            });
            nint handle = window.Handle;
            _check(handle != 0 && _isWindow(handle), "应取得隐藏的原生窗口。");
            _sendMessage(handle, verifyMessage, 6, 9);
            _check(called, "原生消息应路由到窗口回调。");
            window.Dispose();
            _check(window.Handle == 0 && !_isWindow(handle), "销毁后应解除路由并清空窗口句柄。");
        }

        var keys = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        const HotkeyKey f24 = (HotkeyKey)0x87;
        using (var first = new WindowsHotkey(keys, f24))
        {
            _check(first.Handle != 0, "应能注册原生热键。");
        }
        using var second = new WindowsHotkey(keys, f24);
        using Stream data = typeof(Program).Assembly.GetManifestResourceStream("TapGloss.Verify.TrayIcon.ico")
            ?? throw new InvalidOperationException("验证程序缺少托盘图标。");
        using var bytes = new MemoryStream();
        data.CopyTo(bytes);
        nint icon = Win32TrayIcon._load(bytes.ToArray());
        _check(icon != 0 && Win32TrayIcon._destroy(icon), "ICO 应生成可释放的原生图标。");
    }

    // 强制结束测试父进程，确认不监听父进程的测试子进程也会被内核回收。
    private static async Task _testJobAsync()
    {
        _check(Marshal.SizeOf<ChildProcessJob.JobLimits>() == 144, "x64 Job 结构大小应为 144 字节。");
        using Process parent = _startJobFixture("--job-parent");
        Process? child = null;
        try
        {
            string? line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            _check(int.TryParse(line, out int pid), "Job 创建、限制设置和进程分配应成功。");
            child = Process.GetProcessById(pid);
            _check(!child.HasExited, "父进程退出前，测试子进程应保持运行。");
            parent.Kill();
            await parent.WaitForExitAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (!parent.HasExited) parent.Kill();
            if (child is not null)
            {
                if (!child.HasExited) child.Kill();
                child.Dispose();
            }
        }
    }

    // 只在子进程成功加入 Job 后报告 PID。
    private static async Task _runJobParentAsync()
    {
        using Process child = _startJobFixture("--job-child");
        if (!ChildProcessJob._assign(child))
        {
            child.Kill();
            throw new InvalidOperationException("测试子进程无法加入 Job。");
        }
        Console.WriteLine(child.Id);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    // 使用验证程序自己的入口创建测试进程。
    private static Process _startJobFixture(string mode)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TapGloss.Verify.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(mode);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动 Job 验证进程。");
    }

    // 核对测试窗口句柄的生命周期。
    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _isWindow(nint handle);

    // 同线程发送测试消息。
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint _sendMessage(nint window, uint message, nint wParam, nint lParam);

    // 每次只补齐一个缺失项，确认分类和文件名。
    private static string _testBundle(string directory)
    {
        string root = Path.Combine(directory, "amy-bundle-" + Guid.NewGuid().ToString("N"));
        string[] required =
        [
            "voice/en_US-amy-medium.onnx", "voice/tokens.txt", "voice/espeak-ng-data/phontab",
            "voice/espeak-ng-data/phonindex", "voice/espeak-ng-data/phondata", "voice/espeak-ng-data/intonations",
            "sherpa-onnx.dll", "sherpa-onnx-c-api.dll", "onnxruntime.dll",
        ];
        foreach (string file in required)
        {
            try
            {
                AmyVoiceBundle._requireComplete(root);
                throw new InvalidOperationException($"应报告缺少 {file}。");
            }
            catch (SpeechException error)
            {
                _check(error.Kind == SpeechFailureKind.BundleIncomplete && error.Message.Contains(file, StringComparison.Ordinal),
                    "语音资源缺失时应指出精确的文件。");
            }
            string path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1]);
        }
        AmyVoiceBundle._requireComplete(root);
        return root;
    }

    // 核对饱和量化、静音长度和奇数大小的额外 RIFF 块。
    private static byte[] _testWave()
    {
        const int rate = 22050;
        const int leading = 441;
        const int trailing = 882;
        byte[] wave = SpeechWave._encode([-1.5f, 0.25f, 1.5f, float.NaN], rate);
        SpeechWave.Pcm pcm = SpeechWave._read(wave);
        _check(pcm is { Rate: rate, Channels: 1, Align: 2, Offset: 44 }
            && pcm.Length == (leading + 4 + trailing) * sizeof(short), "WAV 头、静音长度或声道不符。");
        ReadOnlySpan<byte> body = wave.AsSpan(pcm.Offset, pcm.Length);
        _check(body[..(leading * 2)].IndexOfAnyExcept((byte)0) == -1
            && body[((leading + 4) * 2)..].IndexOfAnyExcept((byte)0) == -1, "前后静音必须为零。");
        _check(BinaryPrimitives.ReadInt16LittleEndian(body[(leading * 2)..]) == short.MinValue
            && BinaryPrimitives.ReadInt16LittleEndian(body[((leading + 1) * 2)..]) == 8192
            && BinaryPrimitives.ReadInt16LittleEndian(body[((leading + 2) * 2)..]) == short.MaxValue
            && BinaryPrimitives.ReadInt16LittleEndian(body[((leading + 3) * 2)..]) == 0
            && SpeechWave._hasSound(wave), "PCM16 采样限幅或有效幅度不符。");
        _check(!SpeechWave._hasSound(SpeechWave._encode([0], rate)), "静音波形应未通过自检。");

        byte[] withChunk = new byte[wave.Length + 12];
        wave.AsSpan(0, 36).CopyTo(withChunk);
        "JUNK"u8.CopyTo(withChunk.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(withChunk.AsSpan(40), 3);
        withChunk[44] = 5;
        wave.AsSpan(36).CopyTo(withChunk.AsSpan(48));
        BinaryPrimitives.WriteInt32LittleEndian(withChunk.AsSpan(4), withChunk.Length - 8);
        _check(SpeechWave._read(withChunk) == (pcm with { Offset = 56 }), "应跳过额外 RIFF 块及其填充字节。");
        return wave;
    }

    // 模拟断管和合成超时，核对重建一次及连续失败的最终分类。
    private static async Task _testRetryAsync()
    {
        foreach (SpeechFailureKind kind in new[] { SpeechFailureKind.SynthesisFailed, SpeechFailureKind.SynthesisTimeout })
        {
            var failure = new SpeechException(kind, "模拟断管或超时。", retryable: true);
            int calls = 0;
            int resets = 0;
            int output = await AmySpeechClient._withRetryAsync(
                () => ++calls == 1 ? Task.FromException<int>(failure) : Task.FromResult(22), _recycle,
                CancellationToken.None);
            _check(output == 22 && calls == 2 && resets == 1, "暂态失败后应重建并返回第二次合成。");

            calls = resets = 0;
            try
            {
                await AmySpeechClient._withRetryAsync(() =>
                {
                    calls++;
                    return Task.FromException<int>(failure);
                }, _recycle, CancellationToken.None);
                throw new InvalidOperationException("连续两次失败时应抛出异常。");
            }
            catch (SpeechException error)
            {
                _check(error.Kind == kind && calls == 2 && resets == 2, "连续失败时应清理第二条连接并保留故障分类。");
            }

            // 记录每次旧连接由状态机回收。
            Task _recycle(SpeechException _) { resets++; return Task.CompletedTask; }
        }
    }

    // 模拟暂态设备错误与取消，验证等待、Reset、Unprepare 和 Close 的调用顺序。
    private static async Task _testPlaybackAsync(byte[] wave)
    {
        var calls = new List<string>();
        using var cancel = new CancellationTokenSource();
        bool failFirstOpen = true;
        bool cancelOnWrite = false;
        nint submittedHeader = 0;
        int flagsOffset = Marshal.OffsetOf<Win32WaveOutPlayer.Header>(nameof(Win32WaveOutPlayer.Header.Flags)).ToInt32();
        var api = new Win32WaveOutPlayer.Api(_open, (_, _, _) => _record("prepare"),
            (_, header, _) =>
            {
                _record("write");
                submittedHeader = header;
                if (cancelOnWrite) cancel.Cancel();
                else Marshal.WriteInt32(header, flagsOffset, 1);
                return 0;
            },
            _ =>
            {
                Marshal.WriteInt32(submittedHeader, flagsOffset, 1);
                return _record("reset");
            }, (_, _, _) => _record("unprepare"), _ => _record("close"));

        await Win32WaveOutPlayer._playAsync(wave, CancellationToken.None, api, (milliseconds, _) =>
        {
            _check(milliseconds == 150, "设备暂态失败后应等待 150 毫秒。");
            calls.Add("delay");
            return Task.CompletedTask;
        });
        _check(calls.SequenceEqual(["open", "delay", "open", "prepare", "write", "unprepare", "close"]),
            "失败后应该只重试一次，并释放正常完成的播放资源。");

        calls.Clear();
        cancelOnWrite = true;
        try
        {
            await Win32WaveOutPlayer._playAsync(wave, cancel.Token, api);
            throw new InvalidOperationException("取消播放应产生取消状态。");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            _check(calls.SequenceEqual(["open", "prepare", "write", "reset", "unprepare", "close"]),
                "取消后必须先重置，随后解绑头部并关闭设备。");
        }

        // 让第一次设备打开返回暂态错误。
        uint _open(out nint handle, uint id, ref Win32WaveOutPlayer.Format format, nint callback, nuint instance, uint flags)
        {
            calls.Add("open");
            _check(id == uint.MaxValue && format.Bits == 16 && flags == 0x50000, "播放设备或回调模式不符。");
            handle = 1;
            if (!failFirstOpen) return 0;
            failFirstOpen = false;
            return 6;
        }

        // 记录模拟驱动调用。
        uint _record(string name) { calls.Add(name); return 0; }
    }

    // 模拟驱动在 Reset 返回后才通知完成，核对同一次清理就能完成解绑和关闭。
    private static async Task _testAsyncResetAsync(byte[] wave)
    {
        using var cancellation = new CancellationTokenSource();
        using var callback = new EventWaitHandle(false, EventResetMode.AutoReset);
        nint submittedHeader = 0;
        int resetCalls = 0, unprepareCalls = 0, closeCalls = 0;
        int flagsOffset = Marshal.OffsetOf<Win32WaveOutPlayer.Header>(nameof(Win32WaveOutPlayer.Header.Flags)).ToInt32();
        Thread? completion = null;
        var api = new Win32WaveOutPlayer.Api(_open, (_, _, _) => 0, (_, header, _) =>
        {
            submittedHeader = header;
            cancellation.Cancel();
            return 0;
        }, _ =>
        {
            if (++resetCalls == 1)
            {
                completion = new Thread(() =>
                {
                    Thread.Sleep(10);
                    Marshal.WriteInt32(submittedHeader, flagsOffset, 1);
                    callback.Set();
                }) { IsBackground = true };
                completion.Start();
            }
            return 0;
        }, (_, _, _) => { unprepareCalls++; return 0; }, _ => { closeCalls++; return 0; });

        try
        {
            await _expectCanceledAsync(Win32WaveOutPlayer._playAsync(wave, cancellation.Token, api), cancellation.Token);
            _check(resetCalls == 1 && unprepareCalls == 1 && closeCalls == 1,
                "异步置位 WHDR_DONE 后，应在同一次清理中解绑和关闭设备。");
        }
        finally
        {
            completion?.Join();
        }

        // 借用播放器的回调句柄，模拟驱动发出完成事件。
        uint _open(out nint handle, uint id, ref Win32WaveOutPlayer.Format format, nint signal, nuint instance, uint flags)
        {
            callback.SafeWaitHandle = new SafeWaitHandle(signal, ownsHandle: false);
            handle = 1;
            return 0;
        }
    }

    // 模拟驱动各清理阶段失败，检查最终日志、重试上限和新设备的独立播放。
    private static async Task _testCleanupFailuresAsync(byte[] wave)
    {
        var cases = new (string Stage, uint PermanentCode, int Retries, uint FirstCode)[]
        {
            ("waveout-reset", 6, 0, 6),
            ("waveout-unprepare", 5, 0, 5),
            ("waveout-close", 6, 1, 33),
            ("waveout-reset", 5, 1, 0),
            ("waveout-close", 33, 20, 33),
        };
        int flagsOffset = Marshal.OffsetOf<Win32WaveOutPlayer.Header>(nameof(Win32WaveOutPlayer.Header.Flags)).ToInt32();
        foreach (var (stage, permanentCode, retries, firstCode) in cases)
        {
            using var cancellation = new CancellationTokenSource();
            var report = new TaskCompletionSource<(string, uint)>(TaskCreationOptions.RunContinuationsAsynchronously);
            int failures = 0, reports = 0, opened = 0, healthyClosed = 0;
            var api = new Win32WaveOutPlayer.Api(_open, (_, _, _) => 0, (handle, header, _) =>
            {
                if (handle != 1 || stage != "waveout-reset")
                {
                    Marshal.WriteInt32(header, flagsOffset, 1);
                }
                if (handle == 1)
                {
                    cancellation.Cancel();
                }
                return 0;
            }, handle => _result(handle, "waveout-reset"),
                (handle, _, _) => _result(handle, "waveout-unprepare"), handle =>
                {
                    if (handle == 2)
                    {
                        Interlocked.Increment(ref healthyClosed);
                    }
                    return _result(handle, "waveout-close");
                }, (name, code) =>
                {
                    Interlocked.Increment(ref reports);
                    report.TrySetResult((name, code));
                });

            await _expectCanceledAsync(Win32WaveOutPlayer._playAsync(wave, cancellation.Token, api), cancellation.Token);
            await Win32WaveOutPlayer._playAsync(wave, CancellationToken.None, api).WaitAsync(TimeSpan.FromSeconds(2));
            _check(opened == 2 && healthyClosed == 1, "旧设备收尾期间应能独立打开、播放和关闭默认设备。");
            _check(await report.Task.WaitAsync(TimeSpan.FromSeconds(8)) == (stage, permanentCode),
                "停止清理时应报告最终阶段和原生错误码。");
            await Task.Delay(280);
            _check(reports == 1 && failures == retries + 1, "永久错误应立即停止，暂态错误最多重试 20 次。");

            // 按打开顺序为旧设备和健康设备分配独立句柄。
            uint _open(out nint handle, uint id, ref Win32WaveOutPlayer.Format format, nint callback, nuint instance, uint flags)
            {
                handle = Interlocked.Increment(ref opened);
                return 0;
            }

            // 只在旧设备的目标调用注入错误，其余调用正常返回。
            uint _result(nint handle, string phase)
            {
                if (handle != 1 || phase != stage)
                {
                    return 0;
                }
                return Interlocked.Increment(ref failures) == 1 ? firstCode : permanentCode;
            }
        }
    }

    // 规范化和单词判断共用边界，保留大小写并统一词内连字符。
    private static void _testWordNormalization()
    {
        (string? Text, string? Word)[] cases =
        [
            (" compact. ", "compact"),
            ("“thread”", "thread"),
            ("Lock‑Free", "Lock-Free"),
            ("state-of-the-art", "state-of-the-art"),
            (new string('a', 81), null),
            ("two words", null),
            (null, null),
        ];
        foreach (var (text, word) in cases)
        {
            _check(DeepSeekClient.NormalizeWordCandidate(text) == word
                && DeepSeekClient.IsWordCandidate(text) == (word is not null),
                "预合成和单词判断应使用相同的规则与大小写。");
        }
    }

    // 用同一测试程序模拟内部工作进程，保留分开的语音模型占位目录。
    private static string _copyWorkerFixture(string fixture)
    {
        foreach (string file in new[] { "TapGloss.Verify.exe", "TapGloss.Verify.dll", "TapGloss.Verify.deps.json",
                     "TapGloss.Verify.runtimeconfig.json", "WindowsLookup.Core.dll", "Interop.UIAutomationClient.dll",
                     "System.Security.Cryptography.ProtectedData.dll" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(fixture, file));
        }
        return Path.Combine(fixture, "TapGloss.Verify.exe");
    }

    // 验证同一词跨并发等待、取消和后续缓存命中只发送一次合成请求。
    private static async Task _testWordPreparationAsync(string executable)
    {
        string events = _pipeTestEventPrefix(Environment.ProcessId);
        using var received = new EventWaitHandle(false, EventResetMode.ManualReset, events + ".read");
        using var reply = new EventWaitHandle(false, EventResetMode.ManualReset, events + ".reply");
        using var speech = new AmericanSpeech(executable);

        try
        {
            Task<byte[]> other;
            Task<byte[]> shared;
            using (var cancellation = new CancellationTokenSource())
            {
                Task prepare = speech.PrepareWordAsync("cancel", cancellation.Token);
                _check(received.WaitOne(TimeSpan.FromSeconds(3)), "预合成应主动向子进程发送当前单词。");
                received.Reset();
                other = speech._getOrSynthesizeWaveAsync("next", CancellationToken.None);
                shared = speech._getOrSynthesizeWaveAsync("cancel", CancellationToken.None);
                cancellation.Cancel();
                await prepare.WaitAsync(TimeSpan.FromSeconds(3));
                _check(!shared.IsCompleted, "预合成调用方取消等待时，正式朗读应继续等待共享响应。");
            }

            reply.Set();
            byte[] wave = await shared.WaitAsync(TimeSpan.FromSeconds(4));
            _check(wave.SequenceEqual(SpeechWave._encode([0.25f], 22050))
                && (await other.WaitAsync(TimeSpan.FromSeconds(4))).SequenceEqual(SpeechWave._encode([0.5f], 22050)),
                "不同单词应返回各自音频，发起预合成的令牌释放后其余等待仍应成功。");
            await speech.PrepareWordAsync("cancel");
            byte[] cached = await speech._getOrSynthesizeWaveAsync("cancel", CancellationToken.None);
            _check(ReferenceEquals(wave, cached) && !received.WaitOne(0), "同一词只合成一次，重听命中同一缓存。");
        }
        finally
        {
            reply.Set();
        }

        await speech.PrepareWordAsync("failure").WaitAsync(TimeSpan.FromSeconds(4));
        try
        {
            await speech.SpeakAsync("failure").WaitAsync(TimeSpan.FromSeconds(4));
            throw new InvalidOperationException("正式朗读应报告真实合成失败。");
        }
        catch (SpeechException error)
        {
            _check(error.Kind == SpeechFailureKind.SynthesisFailed && error.Code == 2,
                "预合成失败应被观察且不写入缓存，正式朗读重新发送请求并保留错误分类。");
        }
    }

    // 用短空闲时间验证选区子进程复用、退出后重启和主动释放。
    private static async Task _testSelectionLifetimeAsync(string executable)
    {
        using var reader = new SelectionAutomationWorker(executable, TimeSpan.FromMilliseconds(750));
        SelectionCapture first = await reader._readAsync(0, CancellationToken.None);
        _check(int.TryParse(first.Text, out int firstPid), "选区进程应返回测试进程号。");
        using var process = Process.GetProcessById(firstPid);
        SelectionCapture second = await reader._readAsync(0, CancellationToken.None);
        _check(second.Text == first.Text, "空闲时间内应继续复用选区进程。");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));

        SelectionCapture restarted = await reader._readAsync(0, CancellationToken.None);
        _check(int.TryParse(restarted.Text, out int newPid) && newPid != firstPid, "空闲回收后应启动新的进程。");
        using var next = Process.GetProcessById(newPid);
        reader.Dispose();
        await next.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    // 模拟选区进程就绪和逐行响应，用响应正文返回进程号以检查复用。
    private static void _runSelectionFixture()
    {
        Console.WriteLine("READY");
        string response = JsonSerializer.Serialize(new
        {
            Status = CaptureStatus.Selected,
            Text = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
        });
        while (Console.ReadLine() is not null)
        {
            Console.WriteLine(response);
        }
    }

    // 在工作进程实际读到请求之后取消，并在旧响应发出前让下一次请求排队。
    private static async Task _testRequestCancellationAsync(string executable)
    {
        string events = _pipeTestEventPrefix(Environment.ProcessId);
        using var received = new EventWaitHandle(false, EventResetMode.ManualReset, events + ".read");
        using var reply = new EventWaitHandle(false, EventResetMode.ManualReset, events + ".reply");
        using var cancellation = new CancellationTokenSource();
        using var client = new AmySpeechClient(executable);
        client._setMemoryMode(SpeechMemoryMode.Resident);

        byte[] first = await client._synthesizeAsync("first", CancellationToken.None);
        int? pid = client._workerPid();
        _check(pid is not null && SpeechWave._hasSound(first), "模拟 worker 应已启动并完成首次请求。");

        Task<byte[]> cancelled = client._synthesizeAsync("cancel", cancellation.Token);
        try
        {
            _check(received.WaitOne(TimeSpan.FromSeconds(3)), "子进程应已读到待取消的请求。");
            cancellation.Cancel();
            Task<byte[]> next = client._synthesizeAsync("next", CancellationToken.None);
            Task hold = Task.Delay(100);
            _check(await Task.WhenAny(cancelled, next, hold) == hold,
                "取消请求必须读完当前响应，新请求应等待当前帧收尾。");
            reply.Set();
            await _expectCanceledAsync(cancelled, cancellation.Token);
            byte[] wave = await next.WaitAsync(TimeSpan.FromSeconds(3));
            _check(client._workerPid() == pid && wave.SequenceEqual(SpeechWave._encode([0.5f], 22050)),
                "后续请求必须复用原 PID，并读到自己的完整 WAV 帧。");
        }
        finally
        {
            reply.Set();
        }
    }

    // 使用真实命名管道回复带不同采样值的 WAV，并在指定请求处等待测试进程放行。
    private static async Task _runPipeFixtureAsync(string pipeName, int parentPid)
    {
        string events = _pipeTestEventPrefix(parentPid);
        using var received = EventWaitHandle.OpenExisting(events + ".read");
        using var reply = EventWaitHandle.OpenExisting(events + ".reply");
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);

        const int rate = 22050;
        var header = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), rate);
        await pipe.WriteAsync(header);
        var size = new byte[4];
        int failedRequests = 0;
        while (true)
        {
            await pipe.ReadExactlyAsync(size);
            int length = BinaryPrimitives.ReadInt32LittleEndian(size);
            if (length == 0)
            {
                return;
            }
            string word = Encoding.UTF8.GetString(await AmySpeechWorker._readBytesAsync(pipe, length, CancellationToken.None));
            if (word == "cancel")
            {
                received.Set();
                _check(reply.WaitOne(TimeSpan.FromSeconds(4)), "测试应允许被取消请求的响应帧发出。");
            }
            bool failed = word == "failure";
            byte[] payload = failed
                ? Encoding.UTF8.GetBytes(FormattableString.Invariant($"SynthesisFailed:{++failedRequests}"))
                : SpeechWave._encode([word == "next" ? 0.5f : 0.25f], rate);
            header[0] = failed ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
            await pipe.WriteAsync(header);
            await pipe.WriteAsync(payload);
        }
    }

    // 用测试进程 PID 隔离并发测试中的事件名称。
    private static string _pipeTestEventPrefix(int pid) => $@"Local\TapGloss.Verify.Amy.{pid}";

    // 确认操作以预期的取消状态结束，防止成功返回或断管误判为界面取消。
    private static async Task _expectCanceledAsync(Task task, CancellationToken token)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(6));
            throw new InvalidOperationException("应报告取消。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    // 用临时日志检验双文件轮转，并确认原生输出只留下固定分类。
    private static void _testDiagnostics(string directory)
    {
        string root = Path.Combine(directory, "amy-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "speech.log");
        File.WriteAllBytes(path, new byte[SpeechDiagnostics.MaxLogBytes - 8]);
        const string privateWords = "private-word sk-private-test-token";
        SpeechDiagnostics._write(root, "test", SpeechFailureKind.SynthesisFailed, 7,
            [SpeechDiagnostics._classifyNativeLine("onnxruntime " + privateWords)]);
        _check(File.Exists(path + ".old") && new FileInfo(path + ".old").Length <= SpeechDiagnostics.MaxLogBytes
            && new FileInfo(path).Length < SpeechDiagnostics.MaxLogBytes
            && File.ReadAllText(path).Contains("native=onnxruntime")
            && !File.ReadAllText(path).Contains(privateWords), "日志轮转或隐私过滤不符。");
    }

    // 使用真实发布程序验证空闲回收、常驻复用和父进程意外退出。
    private static async Task _testWorkerAsync(string application)
    {
        using (var client = new AmySpeechClient(application, TimeSpan.FromMilliseconds(500)))
        {
            byte[] first = await client._synthesizeAsync("cache", CancellationToken.None);
            _check(SpeechWave._hasSound(first), "发布程序应合成非静音 WAV。");
            using var dynamicWorker = Process.GetProcessById(client._workerPid()!.Value);
            await dynamicWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
            _check(dynamicWorker.ExitCode == 0, "动态模式应发送关闭帧完成退出。");

            client._setMemoryMode(SpeechMemoryMode.Resident);
            byte[] restarted = await client._synthesizeAsync("language", CancellationToken.None);
            _check(SpeechWave._hasSound(restarted), "空闲回收后应能重新加载模型。");
            int pid = client._workerPid()!.Value;
            await Task.Delay(950);
            _check(client._workerPid() == pid, "静态常驻应保留同一个 worker。");
        }

        string verify = Path.Combine(AppContext.BaseDirectory, "TapGloss.Verify.exe");
        var info = new ProcessStartInfo(verify) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--amy-parent");
        info.ArgumentList.Add(application);
        using Process parent = Process.Start(info)!;
        try
        {
            string? answer = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            _check(int.TryParse(answer, out int pid), "测试父进程应返回实际 worker 的进程号。");
            using var orphan = Process.GetProcessById(pid);
            parent.Kill();
            await parent.WaitForExitAsync();
            await orphan.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (!parent.HasExited) parent.Kill();
        }
        Console.WriteLine("发布程序通过实际合成、动态回收重启、常驻复用和父进程退出验证。");
    }

    // 让失败的断言带上对应的行为说明。
    private static void _check(bool passed, string detail)
    {
        if (!passed) throw new InvalidOperationException(detail);
    }
}
