using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsLookup;

/// <summary>尽力备份剪贴板的常见格式，临时复制并核对本次取得的新文字。</summary>
internal static class ClipboardFallback
{
    private const uint UnicodeText = 13;
    private const uint BitmapFormat = 2;
    private const int MaxClipboardBytes = 128 * 1024 * 1024;
    private const string HostedCoreWindow = "Windows.UI.Core.CoreWindow";
    private const uint RootWindow = 2;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 2;
    private const ushort Control = 0x11;
    private const ushort CopyKey = 0x43;
    private static readonly int[] TriggerKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0x54, CopyKey];
    private static readonly HashSet<string> LineCopyEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf", "Antigravity", "devenv", "sublime_text",
        "idea", "idea64", "webstorm", "webstorm64", "rider", "rider64", "clion", "clion64",
        "datagrip", "datagrip64", "goland", "goland64", "phpstorm", "phpstorm64", "studio64",
    };
    private static readonly TimeSpan ClipboardBudget = TimeSpan.FromMilliseconds(2400);
    private static readonly TimeSpan UnownedCopyWindow = TimeSpan.FromMilliseconds(200);
    private static readonly HashSet<string> SnapshotFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "HTML Format", "text/html", "Rich Text Format", "Rich Text Format Without Objects", "text/rtf",
        "PNG", "image/png", "JFIF", "image/jpeg", "GIF", "image/gif",
        "UniformResourceLocator", "UniformResourceLocatorW",
    };
    private static readonly HashSet<string> PrivacyFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard", "ExcludeClipboardContentFromMonitorProcessing",
    };
    private static readonly SemaphoreSlim ClipboardGate = new(1);

    // 为查词限时，已开始的复制仍由后台收尾，其间不允许另一笔操作改动剪贴板。
    internal static async Task<SelectionCapture> _withClipboardBudgetAsync(
        Func<CancellationToken, Task<SelectionCapture>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ClipboardGate.Wait(0))
        {
            return new(CaptureStatus.Unavailable, Detail: "上一笔剪贴板操作尚未结束，请稍后重试。");
        }

        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ClipboardBudget);
        CancellationToken token = deadline.Token;
        Task<SelectionCapture> attempt = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                return await action(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new SelectionCapture(CaptureStatus.Unavailable);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("剪贴板取词结束（{0}）。", ex.GetType().Name);
                return new SelectionCapture(CaptureStatus.Unavailable, Detail: "剪贴板读取失败，请重试。");
            }
            finally
            {
                ClipboardGate.Release();
            }
        });

        try
        {
            SelectionCapture result = await attempt.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(CaptureStatus.Unavailable, Detail: "剪贴板处理超时，请稍后再按 Alt+T。");
        }
        finally
        {
            if (attempt.IsCompleted)
            {
                deadline.Dispose();
            }
            else
            {
                _ = _disposeAfterAsync(attempt, deadline);
            }
        }
    }

    // 外层已经结束等待，后台收尾结束后再释放仍在使用的令牌源。
    private static async Task _disposeAfterAsync(Task attempt, CancellationTokenSource deadline)
    {
        try
        {
            await attempt.ConfigureAwait(false);
        }
        finally
        {
            deadline.Dispose();
        }
    }

    // 退出进程前等待后台操作关闭剪贴板并释放临时备份。
    internal static async Task _waitForIdleAsync()
    {
        await ClipboardGate.WaitAsync().ConfigureAwait(false);
        ClipboardGate.Release();
    }

    // 先备份可独立还原的格式，再复制。发送后完成读取和恢复才响应取消。
    internal static async Task<SelectionCapture> _captureAsync(nint sourceWindow, nint clipboardWindow,
        CancellationToken cancellationToken, nint sourceFocus = 0)
    {
        for (int attempt = 0; TriggerKeys.Any(_isPressed); attempt++)
        {
            if (attempt == 75)
            {
                return new(CaptureStatus.Unavailable, Detail: "请松开快捷键后重新取词。");
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        if (clipboardWindow == 0)
        {
            return new(CaptureStatus.Unavailable, Detail: "无法取得剪贴板，请稍后重试。");
        }
        for (int attempt = 0; !_openClipboard(clipboardWindow); attempt++)
        {
            if (attempt == 2)
            {
                return new(CaptureStatus.Unavailable, Detail: "剪贴板正被占用，请稍后重试。");
            }
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
        uint previousSequence;
        List<(uint Format, nint Handle)>? previousData;
        string? snapshotError;
        try
        {
            previousData = _snapshot(out snapshotError, cancellationToken);
            previousSequence = _sequence();
        }
        finally
        {
            _closeClipboard();
        }
        if (previousData is null)
        {
            return previousSequence == 0
                ? new(CaptureStatus.Unavailable, Detail: snapshotError)
                : new(CaptureStatus.NeedsManualCopy, Detail: snapshotError) { ClipboardSequence = previousSequence };
        }
        try
        {
            if (previousSequence == 0)
            {
                return new(CaptureStatus.Unavailable, Detail: "Windows 未提供当前剪贴板的序列号。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            uint sourceProcess = _getProcess(sourceWindow);
            if (sourceProcess == 0)
            {
                return new(CaptureStatus.Unavailable, Detail: "来源窗口已关闭。");
            }
            uint contentProcess = _getContentProcess(sourceWindow, sourceFocus, sourceProcess);
            if (!SelectionReader._isCurrentSource(sourceWindow, sourceFocus) || previousSequence != _sequence())
            {
                return new(CaptureStatus.Unavailable, Detail: "来源窗口或剪贴板已变化。");
            }
            if (SelectionReader._isNativePassword(sourceFocus))
            {
                return new(CaptureStatus.Protected, Detail: "此控件包含受保护文本。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Input[] keys = [_key(Control, 0), _key(CopyKey, 0), _key(CopyKey, KeyUp), _key(Control, KeyUp)];
            long sentAt = Stopwatch.GetTimestamp();
            uint sent = _sendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
            if (sent is 1 or 2 or 3)
            {
                // 部分按键被发送时补齐释放事件，避免修饰键卡住。
                Input[] release = [_key(CopyKey, KeyUp), _key(Control, KeyUp)];
                _sendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            }
            if (sent == 0)
            {
                return new(CaptureStatus.Unavailable, Detail: "来源应用未接受复制。");
            }

            // 原剪贴板有数据时，即使外层已经超时，也留一段时间接收慢应用的复制结果。
            TimeSpan receiveBudget = TimeSpan.FromMilliseconds(previousData.Count == 0 ? 650 : 1200);
            do
            {
                uint observedSequence = _sequence();
                if (observedSequence != 0 && observedSequence != previousSequence && _openClipboard(clipboardWindow))
                {
                    try
                    {
                        return _readAndRestore(previousSequence, observedSequence, previousData, sourceWindow, sourceFocus,
                            sourceProcess, contentProcess, sentAt, sent == (uint)keys.Length);
                    }
                    finally
                    {
                        _closeClipboard();
                    }
                }
                await Task.Delay(15).ConfigureAwait(false);
            } while (Stopwatch.GetElapsedTime(sentAt) < receiveBudget);
            return new(CaptureStatus.NoSelection, Detail: "来源应用没有返回新的剪贴板文字。");
        }
        finally
        {
            _freeBackup(previousData);
        }
    }

    // 只读本次由原窗口或其内容进程手动复制的新文字，不改写剪贴板。
    internal static SelectionCapture _readManualCopy(nint sourceWindow, nint clipboardWindow, uint previousSequence,
        nint sourceFocus = 0)
    {
        if (!SelectionReader._isCurrentSource(sourceWindow, sourceFocus))
        {
            return new(CaptureStatus.Unavailable, Detail: "来源窗口已切换。");
        }
        if (clipboardWindow == 0 || !_openClipboard(clipboardWindow))
        {
            return new(CaptureStatus.NeedsManualCopy, Detail: "剪贴板正被占用。") { ClipboardSequence = previousSequence };
        }

        try
        {
            uint sourceProcess = _getProcess(sourceWindow);
            SelectionCapture capture = _readFreshText(previousSequence, sourceProcess,
                _getContentProcess(sourceWindow, sourceFocus, sourceProcess));
            return SelectionReader._isCurrentSource(sourceWindow, sourceFocus)
                ? capture
                : new(CaptureStatus.Unavailable, Detail: "读取时来源窗口已切换。");
        }
        finally
        {
            _closeClipboard();
        }
    }

    // 在剪贴板锁内排除旧数据和其他应用的复制，再读取新文本。
    private static SelectionCapture _readFreshText(uint previousSequence, uint sourceProcess, uint focusProcess)
    {
        uint currentSequence = _sequence();
        if (currentSequence == 0)
        {
            return new(CaptureStatus.Unavailable, Detail: "Windows 未提供当前剪贴板的序列号。");
        }
        if (currentSequence == previousSequence)
        {
            return new(CaptureStatus.NeedsManualCopy) { ClipboardSequence = previousSequence };
        }

        if (!_isSourceCopy(previousSequence, currentSequence, _getProcess(_clipboardOwner()), sourceProcess, focusProcess))
        {
            return new(CaptureStatus.NeedsManualCopy, Detail: "剪贴板内容来自其他应用。")
            {
                ClipboardSequence = currentSequence,
            };
        }

        SelectionCapture capture = _readUnicodeText(sourceProcess, automatic: false);
        return capture.Status == CaptureStatus.Copied
            ? capture
            : new(CaptureStatus.NeedsManualCopy, Detail: capture.Detail) { ClipboardSequence = _sequence() };
    }

    // 读取和恢复共用一次剪贴板锁，避免覆盖其他应用随后复制的内容。
    private static SelectionCapture _readAndRestore(uint previousSequence, uint observedSequence,
        List<(uint Format, nint Handle)> previousData, nint sourceWindow, nint sourceFocus,
        uint sourceProcess, uint contentProcess, long sentAt, bool fullShortcutSent)
    {
        nint clipboardOwner = _clipboardOwner();
        uint clipboardProcess = _getProcess(clipboardOwner);
        uint currentSequence = _sequence();
        bool sourceIsCurrent = SelectionReader._isCurrentSource(sourceWindow, sourceFocus);
        // 没有所有者可核对时，只接受在发键后立即出现、焦点可核对且未被再次更改的新文本。
        bool promptUnownedCopy = clipboardOwner == 0 && sourceIsCurrent && sourceFocus != 0 && fullShortcutSent
            && _isPromptUnownedCopy(previousSequence, observedSequence, currentSequence, Stopwatch.GetElapsedTime(sentAt))
            && _isClipboardFormatAvailable(UnicodeText);
        if (!promptUnownedCopy && !_isSourceCopy(previousSequence, currentSequence, clipboardProcess, sourceProcess, contentProcess))
        {
            return new(CaptureStatus.Unavailable, Detail: "无法确认剪贴板内容来自本次复制。");
        }

        if (!sourceIsCurrent && !_canRestoreAfterFocusChange(clipboardOwner, sourceWindow, sourceFocus))
        {
            return new(CaptureStatus.Unavailable, Detail: "输入焦点已切换，当前剪贴板的来源不明确。");
        }

        SelectionCapture captured = new(CaptureStatus.Unavailable);
        bool restored = false;
        try
        {
            if (sourceIsCurrent)
            {
                captured = _readUnicodeText(clipboardProcess == 0 ? sourceProcess : clipboardProcess);
            }
        }
        finally
        {
            if (!promptUnownedCopy || SelectionReader._isCurrentSource(sourceWindow, sourceFocus))
            {
                restored = _restore(previousData);
            }
        }

        if (!sourceIsCurrent || !SelectionReader._isCurrentSource(sourceWindow, sourceFocus))
        {
            return new(CaptureStatus.Unavailable, Detail: "读取时来源窗口已切换。");
        }
        if (!restored)
        {
            return new(CaptureStatus.Unavailable, Detail: "Windows 未能还原已备份的剪贴板格式。");
        }
        return captured.Status == CaptureStatus.NeedsManualCopy
            ? captured with { ClipboardSequence = _sequence() }
            : captured;
    }

    // 在剪贴板锁内核对当前序列号，允许同一次复制连续更新格式。
    internal static bool _isSourceCopy(uint previousSequence, uint currentSequence, uint ownerProcess,
        uint sourceProcess, uint focusProcess) =>
        currentSequence != 0 && currentSequence != previousSequence && ownerProcess != 0
        && (ownerProcess == sourceProcess || ownerProcess == focusProcess);

    // 无所有者时要求首次观察的新序列在取得剪贴板锁后保持一致，并限制发键后的时间。
    internal static bool _isPromptUnownedCopy(uint previousSequence, uint observedSequence, uint currentSequence,
        TimeSpan elapsed) =>
        currentSequence != 0 && currentSequence != previousSequence && currentSequence == observedSequence
        && elapsed <= UnownedCopyWindow;

    // 优先识别原生焦点进程，UWP 宿主的焦点不可用时尝试识别可见的 CoreWindow 子窗口。
    private static uint _getContentProcess(nint sourceWindow, nint focus, uint sourceProcess)
    {
        uint focusProcess = focus != 0 && _getAncestor(focus, RootWindow) == sourceWindow ? _getProcess(focus) : 0;
        if (focusProcess != 0 && focusProcess != sourceProcess)
        {
            return focusProcess;
        }

        nint child = _findChildWindow(sourceWindow, 0, HostedCoreWindow, null);
        if (child == 0 || !_isApplicationFrameHost(sourceProcess))
        {
            return focusProcess;
        }

        uint contentProcess = 0;
        for (; child != 0; child = _findChildWindow(sourceWindow, child, HostedCoreWindow, null))
        {
            if (!_isWindowVisible(child))
            {
                continue;
            }
            uint candidate = _getProcess(child);
            if (candidate == 0 || candidate == sourceProcess)
            {
                continue;
            }
            if (contentProcess != 0 && contentProcess != candidate)
            {
                return focusProcess;
            }
            contentProcess = candidate;
        }
        return contentProcess != 0 ? contentProcess : focusProcess;
    }

    // 只信任 Windows UWP 宿主下的 CoreWindow，普通应用的同名子窗口不参与归属判断。
    private static bool _isApplicationFrameHost(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // 窗口不存在或没有所有者时返回 0。
    private static uint _getProcess(nint window)
    {
        WindowsHotkey._getWindowThreadProcessId(window, out uint processId);
        return processId;
    }

    // 焦点切走后，同一进程可能有其他窗口，只有原控件或已退出前台的原窗口可以恢复。
    private static bool _canRestoreAfterFocusChange(nint clipboardOwner, nint sourceWindow, nint sourceFocus) =>
        (sourceFocus != 0 && clipboardOwner == sourceFocus)
        || (WindowsHotkey._getForegroundWindow() != sourceWindow
            && (clipboardOwner == sourceWindow || _getAncestor(clipboardOwner, RootWindow) == sourceWindow));

    // 逐项备份已知自包含的数据，遇到其他格式或普通格式读取失败时继续备份。
    private static List<(uint Format, nint Handle)>? _snapshot(out string? failure, CancellationToken token)
    {
        var snapshot = new List<(uint Format, nint Handle)>();
        failure = "未能备份原剪贴板。";
        nuint totalSize = 0;
        try
        {
            // 先记录要备份的格式，避免延迟渲染时增补格式影响本次枚举。
            var formats = new List<(uint Format, string Label)>();
            uint format = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Marshal.SetLastPInvokeError(0);
                format = _nextFormat(format);
                if (format == 0)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                    {
                        failure = "Windows 未能列出剪贴板格式。";
                        return null;
                    }
                    break;
                }

                string label = _formatLabel(format);
                if (_isSnapshotFormat(format, label))
                {
                    formats.Add((format, label));
                }
            }

            foreach (var (dataFormat, label) in formats)
            {
                token.ThrowIfCancellationRequested();
                nint handle = _getClipboardData(dataFormat);
                token.ThrowIfCancellationRequested();
                nuint size = _dataSize(dataFormat, handle);
                if (size > MaxClipboardBytes - totalSize)
                {
                    failure = "原剪贴板的数据超出 128 MiB 备份上限。";
                    return null;
                }

                nint duplicate = _duplicateData(dataFormat, handle, size);
                if (duplicate != 0)
                {
                    snapshot.Add((dataFormat, duplicate));
                    totalSize += size;
                }
                else if (PrivacyFormats.Contains(label))
                {
                    // 隐私标记无法复制时保留原剪贴板，避免恢复的数据失去原有的历史或云端限制。
                    failure = "未能备份原剪贴板的隐私标记。";
                    return null;
                }
            }
            failure = null;
            return snapshot;
        }
        finally
        {
            if (failure is not null)
            {
                _freeBackup(snapshot);
            }
        }
    }

    // 仅备份已知有独立复制契约的格式，跳过 OLE 传输数据以及其他未识别的注册格式。
    internal static bool _isSnapshotFormat(uint format, string label) =>
        format is 1 or BitmapFormat or 4 or 5 or 6 or 7 or 8 or 10 or 11 or 12 or UnicodeText or 15 or 16 or 17
        || (format is >= 0xC000 and <= 0xFFFF && (SnapshotFormats.Contains(label) || PrivacyFormats.Contains(label)));

    // 位图是 GDI 句柄，按图像行大小估算占用。其他可支持格式使用全局内存。
    private static nuint _dataSize(uint format, nint handle)
    {
        if (format != BitmapFormat)
        {
            return handle == 0 ? 0 : _globalSize(handle);
        }
        if (_getBitmapInfo(handle, Marshal.SizeOf<BitmapInfo>(), out var bitmap) == 0
            || bitmap.WidthBytes <= 0 || bitmap.Height <= 0 || bitmap.Planes == 0)
        {
            return 0;
        }
        long bytes = (long)bitmap.WidthBytes * bitmap.Height;
        return bytes > MaxClipboardBytes / bitmap.Planes
            ? MaxClipboardBytes + 1
            : (nuint)(bytes * bitmap.Planes);
    }

    // 按实际格式复制句柄，已注册的零字节设置也保留原格式编号。
    private static nint _duplicateData(uint format, nint handle, nuint size)
    {
        if (handle == 0)
        {
            return 0;
        }
        if (size == 0)
        {
            return format >= 0xC000 && _globalFlags(handle) != 0x8000 ? _globalAlloc(2, 0) : 0;
        }
        nint duplicate = _oleDuplicateData(handle, (ushort)format, 2);
        return duplicate != handle ? duplicate : 0;
    }

    // 展示可识别的注册格式名称，内置格式使用固定编号。
    private static string _formatLabel(uint format)
    {
        if (format < 0xC000)
        {
            return $"Windows 格式 {format}";
        }
        var name = new StringBuilder(256);
        return _formatName(format, name, name.Capacity) != 0 ? name.ToString() : "";
    }

    // 在调用者已经打开剪贴板时，只读取本次可查词的字符数。
    private static SelectionCapture _readUnicodeText(uint sourceProcess, bool automatic = true)
    {
        nint handle = _getClipboardData(UnicodeText);
        nuint characters = handle == 0 ? 0 : _globalSize(handle) / sizeof(char);
        int count = (int)Math.Min(characters, (nuint)SelectionReader.MaxTextLength + 1);
        nint source = count == 0 ? 0 : _globalLock(handle);
        if (source == 0)
        {
            return new(CaptureStatus.NoSelection, Detail: "剪贴板没有可读取的文本。");
        }
        try
        {
            string text = Marshal.PtrToStringUni(source, count).Split('\0', 2)[0];
            return _classifyCopiedText(text, () => automatic && _editorCanCopyUnselectedLine(sourceProcess));
        }
        finally
        {
            _globalUnlock(handle);
        }

    }

    // 已知编辑器自动复制的带行尾单行需要手动确认，保留多行选区。
    internal static SelectionCapture _classifyCopiedText(string text, Func<bool> fromLineCopyEditor)
    {
        if (text.Length > SelectionReader.MaxTextLength)
        {
            return new(CaptureStatus.TooLong, Detail: "一次最多处理 1000 个字符。");
        }
        ReadOnlySpan<char> withoutLineEnding = text.AsSpan().TrimEnd("\r\n");
        if (withoutLineEnding.Length < text.Length && !withoutLineEnding.ContainsAny('\r', '\n') && fromLineCopyEditor())
        {
            return new(CaptureStatus.NeedsManualCopy, Detail: "无法区分编辑器选区和自动复制的整行。");
        }
        text = text.Trim();
        return text.Length == 0 ? new(CaptureStatus.NoSelection) : new(CaptureStatus.Copied, text);
    }

    // 已知编辑器的复制命令可能在没有选区时复制当前行。
    private static bool _editorCanCopyUnselectedLine(uint sourceProcess)
    {
        try
        {
            using var process = Process.GetProcessById((int)sourceProcess);
            string name = process.ProcessName;
            return _isLineCopyEditor(name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // 按 Windows 进程名识别可能在没有选区时复制整行的编辑器。
    internal static bool _isLineCopyEditor(string name) =>
        name.StartsWith("pycharm", StringComparison.OrdinalIgnoreCase) || LineCopyEditors.Contains(name);

    // 把预先复制好的句柄交给剪贴板。写入成功的句柄由 Windows 接管。
    private static bool _restore(List<(uint Format, nint Handle)> snapshot)
    {
        if (!_emptyClipboard())
        {
            return false;
        }
        bool complete = true;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            if (_setClipboardData(entry.Format, entry.Handle) != 0
                || _setClipboardData(entry.Format, entry.Handle) != 0)
            {
                snapshot[i] = default;
            }
            else
            {
                complete = false;
            }
        }
        return complete;
    }

    // 只释放仍归程序所有的备份句柄，位图和全局内存使用各自的释放函数。
    private static void _freeBackup(List<(uint Format, nint Handle)> snapshot)
    {
        foreach (var (format, handle) in snapshot)
        {
            if (handle != 0)
            {
                if (format == BitmapFormat)
                {
                    _deleteBitmap(handle);
                }
                else
                {
                    _globalFree(handle);
                }
            }
        }
    }

    /// <summary>检查虚拟键当前是否处于按下状态。</summary>
    private static bool _isPressed(int key) => (_getAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>构造一条 Windows 键盘输入记录。</summary>
    private static Input _key(ushort key, uint flags) =>
        new() { Type = InputKeyboard, Data = new() { Keyboard = new() { VirtualKey = key, Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse; // 为 INPUT 联合体保留正确的大小与对齐。
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal int Type;
        internal int Width;
        internal int Height;
        internal int WidthBytes;
        internal ushort Planes;
        internal ushort BitsPerPixel;
        internal nint Bits;
    }

    [DllImport("user32.dll", EntryPoint = "OpenClipboard")]
    private static extern bool _openClipboard(nint window);

    [DllImport("user32.dll", EntryPoint = "CloseClipboard")]
    private static extern bool _closeClipboard();

    [DllImport("user32.dll", EntryPoint = "EmptyClipboard")]
    private static extern bool _emptyClipboard();

    [DllImport("user32.dll", EntryPoint = "GetClipboardOwner")]
    private static extern nint _clipboardOwner();

    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern nint _getAncestor(nint window, uint relation);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint _findChildWindow(nint parent, nint after, string className, string? title);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _isWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "IsClipboardFormatAvailable")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _isClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static extern uint _sequence();

    [DllImport("user32.dll", EntryPoint = "EnumClipboardFormats", SetLastError = true)]
    private static extern uint _nextFormat(uint format);

    [DllImport("user32.dll", EntryPoint = "GetClipboardFormatNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int _formatName(uint format, StringBuilder name, int size);

    [DllImport("user32.dll", EntryPoint = "GetClipboardData")]
    private static extern nint _getClipboardData(uint format);

    [DllImport("user32.dll", EntryPoint = "SetClipboardData")]
    private static extern nint _setClipboardData(uint format, nint memory);

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short _getAsyncKeyState(int key);

    [DllImport("user32.dll", EntryPoint = "SendInput")]
    private static extern uint _sendInput(uint count, Input[] inputs, int size);

    [DllImport("kernel32.dll", EntryPoint = "GlobalAlloc")]
    private static extern nint _globalAlloc(uint flags, nuint size);

    [DllImport("kernel32.dll", EntryPoint = "GlobalSize")]
    private static extern nuint _globalSize(nint memory);

    [DllImport("kernel32.dll", EntryPoint = "GlobalFlags")]
    private static extern uint _globalFlags(nint memory);

    [DllImport("kernel32.dll", EntryPoint = "GlobalLock")]
    private static extern nint _globalLock(nint memory);

    [DllImport("kernel32.dll", EntryPoint = "GlobalUnlock")]
    private static extern bool _globalUnlock(nint memory);

    [DllImport("kernel32.dll", EntryPoint = "GlobalFree")]
    private static extern nint _globalFree(nint memory);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int _getBitmapInfo(nint bitmap, int length, out BitmapInfo info);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static extern bool _deleteBitmap(nint bitmap);

    [DllImport("ole32.dll", EntryPoint = "OleDuplicateData")]
    private static extern nint _oleDuplicateData(nint data, ushort format, uint flags);
}
