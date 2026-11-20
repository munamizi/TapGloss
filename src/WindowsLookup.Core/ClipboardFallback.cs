using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WindowsLookup;

internal enum EditorCopySelection
{
    Unknown,
    Confirmed,
    Empty,
    Ambiguous,
}

/// <summary>通过剪贴板读取选区、复制译文或将译文粘贴回来源窗口。</summary>
public static partial class ClipboardFallback
{
    private const uint UnicodeText = 13;
    private const uint BitmapFormat = 2;
    private const uint DibFormat = 8;
    private const uint DibV5Format = 17;
    private const int MaxClipboardBytes = 128 * 1024 * 1024;
    private const int MaxEditorMetadataBytes = 256 * 1024;
    private const string VisualStudioLineTag = "VisualStudioEditorOperationsLineCutCopyClipboardTag";
    private const string ChromiumWebCustomData = "Chromium Web Custom MIME Data Format";
    private const string VsCodeEditorData = "vscode-editor-data";
    private const string HostedCoreWindow = "Windows.UI.Core.CoreWindow";
    private const uint RootWindow = 2;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 2;
    private const ushort Control = 0x11;
    private const ushort CopyKey = 0x43;
    private const ushort MenuMaskKey = 0xe8;
    private static readonly int[] TriggerKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0x54, 0x52, CopyKey];
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "WindowsTerminalPreview", "OpenConsole", "conhost", "cmd", "powershell", "pwsh",
        "mintty", "wezterm-gui", "alacritty", "kitty", "ConEmu", "ConEmu64", "Tabby", "Hyper", "putty",
        "FluentTerminal", "Cmder", "MobaXterm", "Xshell", "SecureCRT", "ttermpro", "wsl", "bash", "zsh", "fish",
    };
    private static readonly HashSet<string> VsCodeEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf", "Antigravity",
    };
    private static readonly HashSet<string> LineCopyEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "devenv", "sublime_text",
        "idea", "idea64", "webstorm", "webstorm64", "rider", "rider64", "clion", "clion64",
        "datagrip", "datagrip64", "goland", "goland64", "phpstorm", "phpstorm64", "studio64",
    };
    private static readonly TimeSpan ClipboardBudget = TimeSpan.FromMilliseconds(2400);
    private static readonly TimeSpan UnownedCopyWindow = TimeSpan.FromMilliseconds(200);
    private static readonly HashSet<string> SnapshotFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "HTML Format", "text/html", "Rich Text Format", "Rich Text Format Without Objects", "text/rtf",
        "PNG", "image/png", "JFIF", "image/jpeg", "GIF", "image/gif",
        "UniformResourceLocator", "UniformResourceLocatorW", VisualStudioLineTag, ChromiumWebCustomData,
    };
    private static readonly HashSet<string> PrivacyFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard", "ExcludeClipboardContentFromMonitorProcessing",
    };
    private static readonly SemaphoreSlim ClipboardGate = new(1);

    /// <summary>
    /// 将译文作为独立的 Unicode 文本写入 Windows 剪贴板。
    ///
    /// 步骤：
    ///     1. 等取词或上一次写入释放剪贴板操作权。
    ///     2. 打开剪贴板，被占用时最多重试三次，间隔 15 毫秒。
    ///     3. 写入文本并把内存交给 Windows，应用退出后也能粘贴。
    /// Args:
    ///     clipboardWindow - 可作为剪贴板所有者的窗口句柄，必须在写入结束前保持有效
    ///     text - 要写入的非空译文，可包含换行
    ///     cancellationToken - 取消等待和重试，开始写入后完成本次操作
    /// Returns:
    ///     bool - 成功写入时为 true，窗口无效、文本无法写入或剪贴板持续被占用时为 false。
    /// </summary>
    public static Task<bool> CopyTextAsync(nint clipboardWindow, string text, CancellationToken cancellationToken = default) =>
        _copyTextAsync(clipboardWindow, text, cancellationToken, _tryWriteUnicodeText);

    // 取词恢复和用户主动复制共用一把锁，锁内尝试打开并写入剪贴板。
    internal static Task<bool> _copyTextAsync(nint clipboardWindow, string text, CancellationToken token,
        Func<nint, string, bool?> tryWrite)
    {
        if (clipboardWindow == 0 || string.IsNullOrEmpty(text) || text.Contains('\0'))
        {
            return Task.FromResult(false);
        }

        return Task.Run(async () =>
        {
            await ClipboardGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                for (int attempt = 0; attempt <= 3; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (tryWrite(clipboardWindow, text) is bool copied)
                    {
                        return copied;
                    }
                    if (attempt < 3)
                    {
                        await Task.Delay(15, token).ConfigureAwait(false);
                    }
                }
                return false;
            }
            finally
            {
                ClipboardGate.Release();
            }
        });
    }

    // 无法打开时返回 null 供调用方重试；打开和关闭在同一线程完成。
    private static bool? _tryWriteUnicodeText(nint window, string text)
    {
        if (!_openClipboard(window))
        {
            return null;
        }
        try
        {
            return _writeUnicodeText(text, _emptyClipboard, _setClipboardData);
        }
        finally
        {
            _closeClipboard();
        }
    }

    // 先统一换行并准备带终止符的内存，再写入剪贴板并将句柄交给 Windows。
    internal static bool _writeUnicodeText(string text, Func<bool> empty, Func<uint, nint, nint> set)
    {
        text = text.ReplaceLineEndings("\r\n");
        int terminator = checked(text.Length * sizeof(char));
        nint memory = _globalAlloc(2, (nuint)terminator + sizeof(char));
        if (memory == 0)
        {
            return false;
        }

        try
        {
            nint data = _globalLock(memory);
            if (data == 0)
            {
                return false;
            }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, data, text.Length);
                Marshal.WriteInt16(data, terminator, 0);
            }
            finally
            {
                _globalUnlock(memory);
            }

            if (!empty() || set(UnicodeText, memory) == 0 && set(UnicodeText, memory) == 0)
            {
                return false;
            }
            memory = 0;
            return true;
        }
        finally
        {
            if (memory != 0)
            {
                _globalFree(memory);
            }
        }
    }

    // 为查词限时，已开始的复制仍由后台收尾，其间不允许另一笔操作改动剪贴板。
    internal static async Task<SelectionCapture> _withClipboardBudgetAsync(
        Func<CancellationToken, Task<SelectionCapture>> action, CancellationToken cancellationToken,
        string shortcutLabel = "Alt+T")
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
            return new(CaptureStatus.Unavailable, Detail: $"剪贴板处理超时，请稍后再按 {shortcutLabel}。");
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

    // 松键后备份并锁定来源焦点；发键后只按锁定的来源读取和恢复。
    internal static async Task<SelectionCapture> _captureAsync(nint sourceWindow, nint clipboardWindow,
        CancellationToken cancellationToken, nint sourceFocus = 0, bool quickProbe = false,
        Func<CancellationToken, Task<SelectionCapture>>? recheckFocus = null)
    {
        if (_isTerminalSource(sourceWindow, sourceFocus))
        {
            return SelectionReader._terminalNeedsSelection();
        }

        for (int attempt = 0; _isTriggerKeyPressed(_isPressed); attempt++)
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

        nint verifiedFocus = sourceFocus;
        bool rechecked = false;
        return await _withFreshSnapshotAsync(
            () => _takeSnapshotAsync(sourceWindow, clipboardWindow, cancellationToken),
            _tryCopyAsync, _freeBackup, cancellationToken).ConfigureAwait(false);

        // 返回 null 仅表示尚未发键且序列已变化，外层可重新备份。
        async Task<SelectionCapture?> _tryCopyAsync(List<(uint Format, nint Handle)> data, uint sequence)
        {
            if (_checkCopySource(sourceWindow, out nint focus) is { } issue)
            {
                return issue;
            }
            if (focus != verifiedFocus)
            {
                if (focus == 0 || rechecked || recheckFocus is null)
                {
                    return _unverifiedFocus();
                }

                rechecked = true;
                SelectionCapture updated = await recheckFocus(cancellationToken).ConfigureAwait(false);
                if (_checkCopySource(sourceWindow, out nint currentFocus) is { } recheckIssue)
                {
                    return recheckIssue;
                }
                if (currentFocus != focus)
                {
                    return _unverifiedFocus();
                }
                if (_afterFocusRecheck(updated, () => _canProbeNoSelection(sourceWindow), out quickProbe) is { } result)
                {
                    return result.Status == CaptureStatus.Selected ? result with { VerifiedFocus = focus } : result;
                }
                verifiedFocus = focus;
            }

            uint sourceProcess = _getProcess(sourceWindow);
            if (sourceProcess == 0)
            {
                return new(CaptureStatus.Unavailable, Detail: "来源窗口已关闭。");
            }
            uint contentProcess = _getContentProcess(sourceWindow, verifiedFocus, sourceProcess);
            if (_checkCopySource(sourceWindow, out nint sendFocus) is { } finalIssue)
            {
                return finalIssue;
            }
            if (sendFocus != verifiedFocus)
            {
                return _unverifiedFocus();
            }
            if (_isTriggerKeyPressed(_isPressed))
            {
                return new(CaptureStatus.Unavailable, Detail: "请松开快捷键后重新取词。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (sequence != _sequence())
            {
                return null;
            }
            SelectionCapture copied = await _copyAndRestoreAsync(sourceWindow, clipboardWindow, sendFocus, sourceProcess,
                contentProcess, sequence, data, quickProbe).ConfigureAwait(false);
            return copied.Status == CaptureStatus.Copied ? copied with { VerifiedFocus = sendFocus } : copied;
        }
    }

    // 旧备份先释放，最多等待并重取一次。发键后的任何结果都不再重试。
    internal static async Task<SelectionCapture> _withFreshSnapshotAsync(
        Func<Task<(List<(uint Format, nint Handle)>? Data, uint Sequence, SelectionCapture? Error)>> takeSnapshot,
        Func<List<(uint Format, nint Handle)>, uint, Task<SelectionCapture?>> tryCopy,
        Action<List<(uint Format, nint Handle)>> freeBackup, CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var (data, sequence, error) = await takeSnapshot().ConfigureAwait(false);
            if (data is null)
            {
                return error!;
            }

            try
            {
                if (sequence == 0)
                {
                    return new(CaptureStatus.Unavailable, Detail: "Windows 未提供当前剪贴板的序列号。");
                }
                if (await tryCopy(data, sequence).ConfigureAwait(false) is { } result)
                {
                    return result;
                }
            }
            finally
            {
                freeBackup(data);
            }

            if (attempt == 0)
            {
                await Task.Delay(28, token).ConfigureAwait(false);
            }
        }
        return new(CaptureStatus.Unavailable, Detail: "剪贴板在取词前发生变化，请重试。");
    }

    // 打开剪贴板后在同一线程完成备份、记录序列号和关锁。
    private static async Task<(List<(uint Format, nint Handle)>? Data, uint Sequence, SelectionCapture? Error)>
        _takeSnapshotAsync(nint sourceWindow, nint clipboardWindow, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (_checkCopySource(sourceWindow, out _) is { } issue)
            {
                return (null, 0, issue);
            }
            if (_openClipboard(clipboardWindow))
            {
                break;
            }
            if (attempt == 2)
            {
                return (null, 0, new(CaptureStatus.Unavailable, Detail: "剪贴板正被占用，请稍后重试。"));
            }
            await Task.Delay(15, token).ConfigureAwait(false);
        }

        try
        {
            var data = _snapshot(out string? error, token);
            uint sequence = _sequence();
            if (data is not null)
            {
                return (data, sequence, null);
            }
            var status = sequence == 0 ? CaptureStatus.Unavailable : CaptureStatus.NeedsManualCopy;
            return (null, sequence, new(status, Detail: error) { ClipboardSequence = sequence });
        }
        finally
        {
            _closeClipboard();
        }
    }

    // 有新焦点就要求 UIA 已成功检查安全边界，直接读到的结果无需再复制。
    internal static SelectionCapture? _afterFocusRecheck(SelectionCapture capture, Func<bool> canProbeNoSelection,
        out bool quickProbe)
    {
        quickProbe = capture.Status == CaptureStatus.NoSelection && canProbeNoSelection();
        if (quickProbe || capture.FallbackMode == ClipboardFallbackMode.Automatic)
        {
            return capture.AutomationFocusChecked ? null : _unverifiedFocus();
        }
        return capture;
    }

    // 前台顶层窗口、菜单和原生焦点必须仍在允许发键的状态。
    private static SelectionCapture? _checkCopySource(nint source, out nint focus)
    {
        focus = 0;
        if (!SelectionReader._isCurrentSource(source, 0))
        {
            return new(CaptureStatus.Unavailable, Detail: "来源窗口已切换。");
        }
        if (!WindowsHotkey._tryGetFocusState(source, out focus, out bool inMenu)
            || focus != 0 && !_isFocusInSource(source, focus))
        {
            return _unverifiedFocus();
        }
        if (inMenu)
        {
            return new(CaptureStatus.Unavailable, Detail: "来源窗口正在使用菜单，请返回选区后重试。");
        }
        if (SelectionReader._isNativePassword(focus))
        {
            return new(CaptureStatus.Protected, Detail: "此控件包含受保护文本。");
        }
        return _isTerminalSource(source, focus) ? SelectionReader._terminalNeedsSelection() : null;
    }

    // 只有同一顶层窗口内的非空焦点才有资格替代原焦点。
    internal static bool _isFocusInSource(nint source, nint focus) =>
        source != 0 && focus != 0 && _getAncestor(focus, RootWindow) == source;

    // 焦点改变后无法确认安全性，不向该控件发键。
    internal static SelectionCapture _unverifiedFocus() =>
        new(CaptureStatus.Unavailable, Detail: "来源窗口内的输入焦点已切换，请重新选中文字后重试。");

    // 一旦发键，不受外层取消影响，完成按键释放和剪贴板读取与恢复。
    private static async Task<SelectionCapture> _copyAndRestoreAsync(nint sourceWindow, nint clipboardWindow,
        nint sourceFocus, uint sourceProcess, uint contentProcess, uint previousSequence,
        List<(uint Format, nint Handle)> previousData, bool quickProbe)
    {
        long sentAt = Stopwatch.GetTimestamp();
        (bool pressed, bool released) = await _sendControlChordAsync(CopyKey).ConfigureAwait(false);
        if (!pressed)
        {
            return new(CaptureStatus.Unavailable, Detail: "来源应用未接受复制。");
        }

        TimeSpan receiveBudget = _receiveBudget(quickProbe, previousData.Count > 0);
        do
        {
            uint observedSequence = _sequence();
            if (observedSequence != 0 && observedSequence != previousSequence && _openClipboard(clipboardWindow))
            {
                try
                {
                    return _readAndRestore(previousSequence, observedSequence, previousData, sourceWindow, sourceFocus,
                        sourceProcess, contentProcess, sentAt, released);
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

    // UIA 空选区的探测短等一次，常规回退根据是否有备份为慢应用留出时间。
    internal static TimeSpan _receiveBudget(bool quickProbe, bool hasBackup) =>
        TimeSpan.FromMilliseconds(quickProbe ? 350 : hasBackup ? 1200 : 650);

    // 构造指定 Ctrl 组合键的按下和释放输入。
    private static Task<(bool Pressed, bool Released)> _sendControlChordAsync(ushort key)
    {
        Input[] down = [_key(Control, 0), _key(key, 0)];
        Input[] up = [_key(key, KeyUp), _key(Control, KeyUp)];
        int inputSize = Marshal.SizeOf<Input>();
        return _sendChordAsync(keyUp => _sendInput(2, keyUp ? up : down, inputSize), () => Task.Delay(15));
    }

    // 保留复制流程的组合键测试入口。
    internal static Task<(bool Pressed, bool Released)> _sendCopyChordAsync(Func<bool, uint> sendKeys, Func<Task> hold) =>
        _sendChordAsync(sendKeys, hold);

    // 分两拍注入组合键，等待失败或只按下一部分时也释放，释放不完整时补发。
    internal static async Task<(bool Pressed, bool Released)> _sendChordAsync(Func<bool, uint> sendKeys, Func<Task> hold)
    {
        uint pressed = 0;
        uint released = 0;
        try
        {
            pressed = sendKeys(false);
            if (pressed == 2)
            {
                await hold().ConfigureAwait(false);
            }
        }
        finally
        {
            if (pressed != 0)
            {
                released = sendKeys(true);
                if (released != 2)
                {
                    released = sendKeys(true);
                }
            }
        }
        return (pressed == 2, released == 2);
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

        SelectionCapture capture = _readUnicodeText("");
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
        bool sourceIsCurrent = SelectionReader._isCurrentSource(sourceWindow, sourceFocus, strictFocus: true);
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
                string name = _processName(sourceProcess);
                if (!_isLineCopyEditor(name) && clipboardProcess != 0 && clipboardProcess != sourceProcess)
                {
                    name = _processName(clipboardProcess);
                }
                captured = _readUnicodeText(name);
            }
        }
        finally
        {
            if (SelectionReader._isCurrentSource(sourceWindow, sourceFocus, strictFocus: true)
                || !promptUnownedCopy && _canRestoreAfterFocusChange(clipboardOwner, sourceWindow, sourceFocus))
            {
                restored = _restore(previousData);
            }
        }

        if (!sourceIsCurrent || !SelectionReader._isCurrentSource(sourceWindow, sourceFocus, strictFocus: true))
        {
            return new(CaptureStatus.Unavailable, Detail: SelectionReader._isCurrentSource(sourceWindow, 0)
                ? "读取时输入焦点已切换。" : "来源窗口已切换。");
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
    private static bool _isApplicationFrameHost(uint processId) =>
        _processName(processId).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);

    // 识别独立终端和原生控制台，焦点与前台窗口来自不同进程时也核对焦点。
    private static bool _isTerminalSource(nint sourceWindow, nint sourceFocus) =>
        _isTerminalWindow(sourceWindow) || (sourceFocus != 0 && sourceFocus != sourceWindow && _isTerminalWindow(sourceFocus));

    // 先检查原生终端类，再按拥有窗口的可执行程序名识别终端。
    private static bool _isTerminalWindow(nint window) =>
        _isTerminalWindowClass(SelectionReader._getWindowClass(window)) || _isTerminalProcess(_processName(_getProcess(window)));

    // 标准控制台和 Windows Terminal 的原生宿主类。
    internal static bool _isTerminalWindowClass(string name) =>
        name.Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase);

    // 用不带扩展名的 Windows 进程名识别已知终端。
    internal static bool _isTerminalProcess(string name) => TerminalProcesses.Contains(name);

    // 识别不了整行标记的编辑器，优先信任 UIA 已经判断出的空选区。
    internal static bool _canProbeNoSelection(string name) =>
        !_isTerminalProcess(name) && (!_isLineCopyEditor(name) || _hasEditorCopyMarker(name));

    // 只有已知的 VS Code 系应用使用编号开头的终端输入框标签。
    internal static bool _isVsCodeEditor(nint window) => VsCodeEditors.Contains(_processName(_getProcess(window)));

    // 按前台窗口的进程名决定是否进行空选区探测。
    internal static bool _canProbeNoSelection(nint window) =>
        _canProbeNoSelection(_processName(_getProcess(window)));

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

    // 先枚举当前格式，再读取数据，避免延迟渲染增补格式时影响枚举。
    private static List<(uint Format, nint Handle)>? _snapshot(out string? failure, CancellationToken token,
        bool requireRestorable = false)
    {
        var formats = new List<(uint Format, string Label)>();
        bool hasFormats = false;
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

            hasFormats = true;
            string label = _formatLabel(format);
            if (_isSnapshotFormat(format, label))
            {
                formats.Add((format, label));
            }
        }

        return _backupClipboardFormats(formats, hasFormats, requireRestorable, token, _getClipboardData,
            _dataSize, _duplicateData, _freeBackup, out failure);
    }

    // 普通格式失败时继续备份，隐私标记丢失或非空剪贴板完全无法恢复时拒绝替换。
    internal static List<(uint Format, nint Handle)>? _backupClipboardFormats(List<(uint Format, string Label)> formats,
        bool hasFormats, bool requireRestorable, CancellationToken token, Func<uint, nint> getData,
        Func<uint, nint, nuint> dataSize, Func<uint, nint, nuint, nint> duplicateData,
        Action<List<(uint Format, nint Handle)>> freeBackup, out string? failure)
    {
        var snapshot = new List<(uint Format, nint Handle)>();
        failure = "未能备份原剪贴板。";
        nuint totalSize = 0;
        try
        {
            _prioritizeBitmapFormats(formats);
            bool bitmapBackedUp = false;
            foreach (var (format, label) in formats)
            {
                token.ThrowIfCancellationRequested();
                if (bitmapBackedUp && _isBitmapFamily(format))
                {
                    continue;
                }

                nint handle = getData(format);
                token.ThrowIfCancellationRequested();
                nuint size = dataSize(format, handle);
                if (size > MaxClipboardBytes - totalSize)
                {
                    failure = "原剪贴板的数据超出 128 MiB 备份上限。";
                    return null;
                }

                nint duplicate = duplicateData(format, handle, size);
                if (duplicate != 0)
                {
                    snapshot.Add((format, duplicate));
                    totalSize += size;
                    bitmapBackedUp |= _isBitmapFamily(format);
                }
                else if (PrivacyFormats.Contains(label))
                {
                    failure = "未能备份原剪贴板的隐私标记。";
                    return null;
                }
            }
            if (requireRestorable && hasFormats && snapshot.Count == 0)
            {
                failure = "原剪贴板中没有可还原的数据格式。";
                return null;
            }
            failure = null;
            return snapshot;
        }
        finally
        {
            if (failure is not null)
            {
                freeBackup(snapshot);
            }
        }
    }

    // 在第一个位图的位置优先尝试 DIBV5、DIB、位图，保持 PNG 等独立格式的原有顺序。
    internal static void _prioritizeBitmapFormats(List<(uint Format, string Label)> formats)
    {
        int first = formats.FindIndex(entry => _isBitmapFamily(entry.Format));
        if (first < 0)
        {
            return;
        }
        var bitmaps = formats.Where(entry => _isBitmapFamily(entry.Format))
            .OrderByDescending(entry => entry.Format).ToArray();
        formats.RemoveAll(entry => _isBitmapFamily(entry.Format));
        formats.InsertRange(first, bitmaps);
    }

    // Windows 可以按需在这三种位图表示之间转换。
    internal static bool _isBitmapFamily(uint format) => format is BitmapFormat or DibFormat or DibV5Format;

    // 仅备份已知有独立复制契约的格式，跳过 OLE 传输数据以及其他未识别的注册格式。
    internal static bool _isSnapshotFormat(uint format, string label) =>
        format is 1 or BitmapFormat or 4 or 5 or 6 or 7 or DibFormat or 10 or 11 or 12 or UnicodeText or 15 or 16 or DibV5Format
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
    private static SelectionCapture _readUnicodeText(string sourceName)
    {
        EditorCopySelection selection = _readEditorCopySelection(sourceName);
        if (selection == EditorCopySelection.Empty)
        {
            return new(CaptureStatus.NoSelection);
        }

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
            return _classifyCopiedText(text, () => _isLineCopyEditor(sourceName), selection);
        }
        finally
        {
            _globalUnlock(handle);
        }

    }

    // 先信任明确的编辑器标记，缺少标记时仅对已知编辑器使用单行行尾规则。
    internal static SelectionCapture _classifyCopiedText(string text, Func<bool> fromLineCopyEditor,
        EditorCopySelection selection = EditorCopySelection.Unknown)
    {
        if (selection == EditorCopySelection.Empty)
        {
            return new(CaptureStatus.NoSelection);
        }
        if (text.Length > SelectionReader.MaxTextLength)
        {
            return new(CaptureStatus.TooLong, Detail: "一次最多处理 1000 个字符。");
        }
        if (selection == EditorCopySelection.Ambiguous)
        {
            return new(CaptureStatus.NeedsManualCopy, Detail: "多光标复制可能包含未选中的整行。");
        }
        ReadOnlySpan<char> withoutLineEnding = text.AsSpan().TrimEnd("\r\n");
        if (selection == EditorCopySelection.Unknown && withoutLineEnding.Length < text.Length
            && !withoutLineEnding.ContainsAny('\r', '\n') && fromLineCopyEditor())
        {
            return new(CaptureStatus.NeedsManualCopy, Detail: "无法区分编辑器选区和自动复制的整行。");
        }
        text = text.Trim();
        return text.Length == 0 ? new(CaptureStatus.NoSelection) : new(CaptureStatus.Copied, text);
    }

    // 仅在已核验的编辑器复制中读取对应的注册标记，调用方持有剪贴板锁。
    private static EditorCopySelection _readEditorCopySelection(string name)
    {
        if (name.Equals("devenv", StringComparison.OrdinalIgnoreCase))
        {
            uint lineTag = _registerClipboardFormat(VisualStudioLineTag);
            return _visualStudioCopySelection(lineTag != 0, lineTag != 0 && _isClipboardFormatAvailable(lineTag));
        }
        if (!VsCodeEditors.Contains(name))
        {
            return EditorCopySelection.Unknown;
        }

        uint format = _registerClipboardFormat(ChromiumWebCustomData);
        if (format == 0 || !_isClipboardFormatAvailable(format))
        {
            return EditorCopySelection.Unknown;
        }
        nint handle = _getClipboardData(format);
        nuint size = handle == 0 ? 0 : _globalSize(handle);
        nint data = size >= 8 ? _globalLock(handle) : 0;
        if (data == 0)
        {
            return EditorCopySelection.Unknown;
        }
        try
        {
            int payloadLength = Marshal.ReadInt32(data);
            if (payloadLength < 4 || payloadLength > MaxEditorMetadataBytes || (nuint)(4 + payloadLength) > size)
            {
                return EditorCopySelection.Unknown;
            }
            // GlobalSize 可能包含分配器尾部填充，只复制 Pickle 头和声明的负载。
            byte[] pickle = new byte[4 + payloadLength];
            Marshal.Copy(data, pickle, 0, pickle.Length);
            return _readCodeClipboardSelection(pickle);
        }
        finally
        {
            _globalUnlock(handle);
        }
    }

    // Visual Studio 的整行复制标记只需检查存在性，注册失败时按未知数据处理。
    internal static EditorCopySelection _visualStudioCopySelection(bool formatRegistered, bool lineTagPresent) =>
        !formatRegistered ? EditorCopySelection.Unknown : lineTagPresent ? EditorCopySelection.Empty : EditorCopySelection.Confirmed;

    // 读取 Chromium Windows Pickle 中的版本 1 编辑器元数据，损坏、缺失或未知版本都回退。
    internal static EditorCopySelection _readCodeClipboardSelection(ReadOnlySpan<byte> pickle)
    {
        if (pickle.Length < 8)
        {
            return EditorCopySelection.Unknown;
        }
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(pickle);
        if (payloadLength < 4 || payloadLength > MaxEditorMetadataBytes
            || payloadLength % 4 != 0 || payloadLength > pickle.Length - 4)
        {
            return EditorCopySelection.Unknown;
        }
        ReadOnlySpan<byte> payload = pickle.Slice(4, payloadLength);
        uint entries = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (entries > (uint)(payload.Length - 4) / 8)
        {
            return EditorCopySelection.Unknown;
        }

        int offset = 4;
        ReadOnlySpan<byte> metadata = default;
        bool found = false;
        for (uint index = 0; index < entries; index++)
        {
            if (!_readPickleString(payload, ref offset, out var key) || !_readPickleString(payload, ref offset, out var value))
            {
                return EditorCopySelection.Unknown;
            }
            if (MemoryMarshal.Cast<byte, char>(payload[key]).SequenceEqual(VsCodeEditorData.AsSpan()))
            {
                if (found)
                {
                    return EditorCopySelection.Unknown;
                }
                metadata = payload[value];
                found = true;
            }
        }
        if (metadata.IsEmpty || offset != payload.Length)
        {
            return EditorCopySelection.Unknown;
        }

        try
        {
            using var json = JsonDocument.Parse(Encoding.Unicode.GetString(metadata));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int number) || number != 1
                || !root.TryGetProperty("isFromEmptySelection", out var empty))
            {
                return EditorCopySelection.Unknown;
            }
            if (empty.ValueKind == JsonValueKind.True)
            {
                return EditorCopySelection.Empty;
            }
            if (empty.ValueKind != JsonValueKind.False)
            {
                return EditorCopySelection.Unknown;
            }
            if (root.TryGetProperty("multicursorText", out var parts) && parts.ValueKind == JsonValueKind.Array
                && parts.EnumerateArray().Any(part => part.ValueKind == JsonValueKind.String
                    && part.GetString() is { Length: > 0 } text && text[^1] is '\r' or '\n'))
            {
                return EditorCopySelection.Ambiguous;
            }
            return EditorCopySelection.Confirmed;
        }
        catch (JsonException)
        {
            return EditorCopySelection.Unknown;
        }
    }

    // Pickle 的字符串长度以 UTF-16 码元计，字节区间向上对齐到 4 字节。
    private static bool _readPickleString(ReadOnlySpan<byte> payload, ref int offset, out Range value)
    {
        value = default;
        if (payload.Length - offset < 4)
        {
            return false;
        }
        int units = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += 4;
        if (units < 0 || units > (payload.Length - offset) / 2)
        {
            return false;
        }
        int bytes = units * 2;
        int padded = (bytes + 3) & ~3;
        if (padded > payload.Length - offset)
        {
            return false;
        }
        value = offset..(offset + bytes);
        offset += padded;
        return true;
    }

    // 读取不带扩展名的进程名，进程退出或不可访问时返回空字符串。
    private static string _processName(uint processId)
    {
        if (processId == 0)
        {
            return "";
        }
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    // Visual Studio 和 VS Code 系可用原生剪贴板标记识别部分空选区复制。
    private static bool _hasEditorCopyMarker(string name) =>
        name.Equals("devenv", StringComparison.OrdinalIgnoreCase) || VsCodeEditors.Contains(name);

    // 按 Windows 进程名识别可能在没有选区时复制整行的编辑器。
    internal static bool _isLineCopyEditor(string name) =>
        name.StartsWith("pycharm", StringComparison.OrdinalIgnoreCase) || LineCopyEditors.Contains(name) || VsCodeEditors.Contains(name);

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

    // Ctrl+C 发送前等待修饰键、查词快捷键的主键和 C 全部松开。
    internal static bool _isTriggerKeyPressed(Func<int, bool> isPressed) => TriggerKeys.Any(isPressed);

    // 检查虚拟键当前是否处于按下状态。
    internal static bool _isPressed(int key) => (_getAsyncKeyState(key) & 0x8000) != 0;

    // 未分配虚拟键与按住的 Alt 构成组合键；若 Windows 只接受了按下，就补发释放。
    internal static void _maskAltMenu()
    {
        Input down = _key(MenuMaskKey, 0);
        Input up = _key(MenuMaskKey, KeyUp);
        int size = Marshal.SizeOf<Input>();
        if (_sendInput(2, [down, up], size) == 1)
        {
            _sendInput(1, [up], size);
        }
    }

    /// <summary>构造一条 Windows 键盘输入记录。</summary>
    private static Input _key(ushort key, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new() { Keyboard = new() { VirtualKey = key, ScanCode = (ushort)_mapVirtualKey(key, 0), Flags = flags } },
    };

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

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW", ExactSpelling = true)]
    private static extern uint _mapVirtualKey(uint key, uint mapType);

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint _registerClipboardFormat(string name);

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
