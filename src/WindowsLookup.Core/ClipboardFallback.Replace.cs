using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipboardBackup = System.Collections.Generic.List<(uint Format, nint Handle)>;

namespace WindowsLookup;

public enum ReplaceStatus
{
    Succeeded,
    InvalidTarget,
    TerminalBlocked,
    ClipboardBusy,
    ClipboardSnapshotFailed,
    ClipboardChanged,
    FocusLost,
    PasteRejected,
}

/// <summary>记录替换结果、失败原因及译文是否仍在剪贴板中。</summary>
public sealed record ReplaceResult(ReplaceStatus Status, string? Detail = null, bool LeftInClipboard = false);

public static partial class ClipboardFallback
{
    private const ushort PasteKey = 0x56;
    private const int ReplaceClipboardAttempts = 4;
    private static readonly int[] PasteTriggerKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C, PasteKey];

    /// <summary>
    /// 将英文译文粘贴回发起查词的窗口。
    ///
    /// 步骤：
    ///     1. 校验来源，需要时备份原剪贴板，再写入译文。
    ///     2. 请调用方切回来源并关窗，等待松键和焦点稳定，再核对剪贴板并发送 Ctrl+V。
    ///     3. 粘贴后按需还原原剪贴板，并释放备份。
    /// Args:
    ///     sourceWindow - 本次查词来自的顶层窗口。
    ///     sourceFocus - 取词时最后核验的原生焦点，可为 0。
    ///     clipboardWindow - 写入剪贴板时使用的消息窗口，须在任务结束前有效。
    ///     text - 可包含换行的英文译文。
    ///     switchToSourceAndHide - 在界面线程切回来源并关窗，返回 true 表示已进入粘贴阶段。
    ///     isTerminal - 取词时是否已识别为终端。
    ///     restoreClipboard - 默认备份并在粘贴后恢复原剪贴板，设为 false 时按普通复制写入译文。
    ///     cancellationToken - 取消关窗前的准备，关窗后的检查和收尾继续执行。
    /// Returns:
    ///     ReplaceResult - 操作状态，以及失败时译文是否仍可手动粘贴。
    /// </summary>
    public static Task<ReplaceResult> ReplaceSelectionAsync(
        nint sourceWindow, nint sourceFocus, nint clipboardWindow, string text, Func<Task<bool>> switchToSourceAndHide,
        bool isTerminal = false, bool restoreClipboard = true, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            uint sourceProcess = _getProcess(sourceWindow);
            nint safeFocus = 0;

            var clipboard = new ReplaceClipboardOperations(
                () => _openClipboard(clipboardWindow), () => _closeClipboard(),
                token =>
                {
                    ClipboardBackup? backup = _snapshot(out string? failure, token, requireRestorable: true);
                    return (backup, failure);
                },
                temporary => _writeTranslation(text, temporary), () => _clipboardOwner() == clipboardWindow,
                _sequence, _restore, _freeBackup);

            return _replaceSelectionAsync(_checkTarget, clipboard, switchToSourceAndHide,
                _waitForKeysAsync, _waitForSafeFocusAsync,
                () => _tryGetPasteFocus(sourceWindow, sourceProcess, out nint focus) && focus == safeFocus
                    && !PasteTriggerKeys.Any(_isPressed) && WindowsHotkey._getForegroundWindow() == sourceWindow,
                () => _sendControlChordAsync(PasteKey), Task.Delay, restoreClipboard, cancellationToken);

            // 关窗前确认目标仍存在，并检查最后记录和当前可取得的原生焦点。
            ReplaceResult? _checkTarget()
            {
                if (!_isReplaceTargetAvailable(sourceWindow) || sourceProcess == 0 || _getProcess(sourceWindow) != sourceProcess)
                {
                    return new(ReplaceStatus.InvalidTarget, "原窗口已关闭、最小化或不可见，请使用复制按钮。");
                }
                nint currentFocus = WindowsHotkey.GetFocusedWindow(sourceWindow);
                if (isTerminal || _isTerminalSource(sourceWindow, sourceFocus) || _isTerminalSource(sourceWindow, currentFocus))
                {
                    return new(ReplaceStatus.TerminalBlocked, "终端窗口不支持直接替换，请使用复制按钮。");
                }
                if (SelectionReader._isNativePassword(sourceFocus) || SelectionReader._isNativePassword(currentFocus))
                {
                    return new(ReplaceStatus.InvalidTarget, "密码输入框不支持直接替换，请使用复制按钮。");
                }
                if (clipboardWindow == 0 || string.IsNullOrEmpty(text) || text.Contains('\0'))
                {
                    return new(ReplaceStatus.InvalidTarget, "无法将本次译文用于替换，请使用复制按钮。");
                }
                return null;
            }

            // 单独等待修饰键和 V 键松开，焦点预算在松键后开始。
            Task<bool> _waitForKeysAsync()
            {
                long started = Stopwatch.GetTimestamp();
                return _waitForPasteKeysAsync(() => PasteTriggerKeys.Any(_isPressed),
                    () => Stopwatch.GetElapsedTime(started), () => Task.Delay(10));
            }

            // 等待原窗口重新激活，并记录发键前要复核的原生焦点。
            async Task<bool> _waitForSafeFocusAsync()
            {
                long started = Stopwatch.GetTimestamp();
                var (ready, focus) = await _waitForPasteFocusAsync(
                    () => _tryGetPasteFocus(sourceWindow, sourceProcess, out nint current) ? current : null,
                    sourceFocus, _isFocusInSource(sourceWindow, sourceFocus), () => Stopwatch.GetElapsedTime(started),
                    () => Task.Delay(10)).ConfigureAwait(false);
                safeFocus = focus;
                return ready;
            }
        });

    // 管理关窗前的可取消准备，以及关窗后与浮窗生命周期无关的粘贴和清理。
    internal static async Task<ReplaceResult> _replaceSelectionAsync(Func<ReplaceResult?> checkTarget,
        ReplaceClipboardOperations clipboard, Func<Task<bool>> switchToSourceAndHide,
        Func<Task<bool>> waitForKeys, Func<Task<bool>> waitForFocus, Func<bool> canPaste,
        Func<Task<(bool Pressed, bool Released)>> sendPaste,
        Func<int, CancellationToken, Task> delay, bool restoreClipboard, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (checkTarget() is { } invalidTarget)
        {
            return invalidTarget;
        }
        await ClipboardGate.WaitAsync(token).ConfigureAwait(false);
        ClipboardBackup? backup = null;
        uint writtenSequence = 0;
        bool committed = false;
        try
        {
            if (checkTarget() is { } changedTarget)
            {
                return changedTarget;
            }
            ReplaceResult? preparationError = null;
            if (!await _retryReplaceClipboardAsync(_tryPrepareClipboard, delay, token).ConfigureAwait(false))
            {
                return new(ReplaceStatus.ClipboardBusy, "剪贴板正被占用，请重试或使用复制按钮。");
            }
            if (preparationError is not null)
            {
                return preparationError;
            }

            token.ThrowIfCancellationRequested();
            if (!_ownsReplacementClipboard(clipboard, writtenSequence))
            {
                return new(ReplaceStatus.ClipboardChanged, "剪贴板内容已变化，请重试替换。");
            }
            if (checkTarget() is { } finalTargetIssue)
            {
                return finalTargetIssue;
            }
            committed = await switchToSourceAndHide().ConfigureAwait(false);
            if (!committed)
            {
                return new(ReplaceStatus.InvalidTarget, "无法切回原窗口，请使用复制按钮。");
            }

            // 切窗后不能再使用浮窗的取消令牌，否则关闭浮窗会中断粘贴。
            if (!await waitForKeys().ConfigureAwait(false))
            {
                return _replaceFailure(ReplaceStatus.FocusLost, "请松开修饰键和 V 键后重试。");
            }
            if (!await waitForFocus().ConfigureAwait(false) || !canPaste())
            {
                return _replaceFailure(ReplaceStatus.FocusLost, "无法确认原窗口的输入焦点。");
            }
            if (!_ownsReplacementClipboard(clipboard, writtenSequence))
            {
                return new(ReplaceStatus.ClipboardChanged, "剪贴板内容已变化，已停止自动粘贴。");
            }
            var (pressed, released) = await sendPaste().ConfigureAwait(false);
            if (!pressed || !released)
            {
                return _replaceFailure(ReplaceStatus.PasteRejected,
                    pressed ? "请检查原窗口是否已粘贴译文。" : "原窗口未接受自动粘贴。");
            }

            if (restoreClipboard)
            {
                await delay(280, CancellationToken.None).ConfigureAwait(false);
                await _restoreReplacementClipboardAsync(clipboard, backup!, writtenSequence, delay).ConfigureAwait(false);
            }
            return new(ReplaceStatus.Succeeded);

            // 从打开到关闭都在同一线程完成，只有剪贴板被占用时才异步等待重试。
            bool _tryPrepareClipboard()
            {
                if (!clipboard.Open())
                {
                    return false;
                }
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (restoreClipboard)
                    {
                        var (snapshot, failure) = clipboard.Snapshot(token);
                        if (snapshot is null)
                        {
                            preparationError = new(ReplaceStatus.ClipboardSnapshotFailed,
                                (failure ?? "未能备份原剪贴板。") + "请使用复制按钮。");
                            return true;
                        }
                        backup = snapshot;
                    }

                    token.ThrowIfCancellationRequested();
                    var (written, changed) = clipboard.Write(restoreClipboard);
                    if (!written)
                    {
                        if (changed && backup is not null && !clipboard.Restore(backup))
                        {
                            Trace.TraceWarning("译文写入失败后未能完整还原剪贴板。");
                        }
                        preparationError = new(ReplaceStatus.ClipboardBusy, "无法暂存译文，请重试或使用复制按钮。");
                        return true;
                    }
                }
                finally
                {
                    clipboard.Close();
                }

                // Windows 关锁时可能合成文本格式并更新序列号，确认译文仍由本窗口持有后再记录。
                if (!clipboard.IsOwned())
                {
                    preparationError = new(ReplaceStatus.ClipboardChanged, "剪贴板内容已变化，请重试替换。");
                    return true;
                }
                writtenSequence = clipboard.Sequence();
                if (writtenSequence == 0)
                {
                    preparationError = new(ReplaceStatus.ClipboardBusy, "无法暂存译文，请重试或使用复制按钮。");
                }
                return true;
            }

            // 只有所有者和序列号都匹配时，才提示用户还能手动粘贴译文。
            ReplaceResult _replaceFailure(ReplaceStatus status, string detail) =>
                new(status, detail, LeftInClipboard: _ownsReplacementClipboard(clipboard, writtenSequence));
        }
        finally
        {
            try
            {
                if (backup is not null)
                {
                    try
                    {
                        if (!committed && writtenSequence != 0)
                        {
                            await _restoreReplacementClipboardAsync(clipboard, backup, writtenSequence, delay).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        clipboard.Free(backup);
                    }
                }
            }
            finally
            {
                ClipboardGate.Release();
            }
        }
    }

    // 用所有者和序列号确认剪贴板仍是本次写入的译文。
    private static bool _ownsReplacementClipboard(ReplaceClipboardOperations clipboard, uint expectedSequence) =>
        clipboard.Sequence() == expectedSequence && clipboard.IsOwned();

    // 等待粘贴相关按键松开，连续按住 500 毫秒时中止。
    internal static async Task<bool> _waitForPasteKeysAsync(Func<bool> keysPressed, Func<TimeSpan> elapsed, Func<Task> wait)
    {
        while (elapsed() < TimeSpan.FromMilliseconds(500))
        {
            if (!keysPressed())
            {
                return true;
            }
            await wait().ConfigureAwait(false);
        }
        return false;
    }

    // 先等待原焦点，120 毫秒后允许同一顶层窗口内的宿主焦点变化，要求稳定 30 毫秒。
    internal static async Task<(bool Ready, nint Focus)> _waitForPasteFocusAsync(
        Func<nint?> readFocus, nint originalFocus, bool preferOriginal, Func<TimeSpan> elapsed, Func<Task> wait)
    {
        nint? stableFocus = null;
        TimeSpan stableSince = TimeSpan.Zero;
        TimeSpan now;
        while ((now = elapsed()) < TimeSpan.FromMilliseconds(350))
        {
            nint? focus = readFocus();
            if (focus is null || preferOriginal && focus != originalFocus && now < TimeSpan.FromMilliseconds(120))
            {
                stableFocus = null;
            }
            else if (stableFocus != focus)
            {
                stableFocus = focus;
                stableSince = now;
            }
            else if (now - stableSince >= TimeSpan.FromMilliseconds(30))
            {
                return (true, focus.Value);
            }
            await wait().ConfigureAwait(false);
        }
        return (false, 0);
    }

    // 仅使用原窗口的焦点，并排除菜单、原生密码框和终端。
    private static bool _tryGetPasteFocus(nint source, uint sourceProcess, out nint focus)
    {
        focus = 0;
        return WindowsHotkey._getForegroundWindow() == source && _isReplaceTargetAvailable(source)
            && _getProcess(source) == sourceProcess
            && WindowsHotkey._tryGetFocusState(source, out focus, out bool inMenu) && !inMenu
            && (focus == 0 || _isFocusInSource(source, focus))
            && !SelectionReader._isNativePassword(focus) && !_isTerminalSource(source, focus);
    }

    // 剪贴板由其他应用短暂占用时重试，关窗前的重试可随浮窗取消。
    private static async Task<bool> _retryReplaceClipboardAsync(Func<bool> attemptOperation,
        Func<int, CancellationToken, Task> delay, CancellationToken token)
    {
        for (int attempt = 1; attempt <= ReplaceClipboardAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (attemptOperation())
            {
                return true;
            }
            if (attempt < ReplaceClipboardAttempts)
            {
                await delay(25, token).ConfigureAwait(false);
            }
        }
        return false;
    }

    // 在同一线程打开、核对并关闭剪贴板，避免覆盖替换期间新复制的数据。
    private static async Task _restoreReplacementClipboardAsync(ReplaceClipboardOperations clipboard,
        ClipboardBackup backup, uint expectedSequence, Func<int, CancellationToken, Task> delay)
    {
        if (!await _retryReplaceClipboardAsync(_tryRestoreClipboard, delay, CancellationToken.None).ConfigureAwait(false))
        {
            Trace.TraceWarning("替换后剪贴板持续被占用，未能还原备份。");
        }

        // 每次重新打开前后都检查序列号，打开成功时在同一线程关闭。
        bool _tryRestoreClipboard()
        {
            if (!_ownsReplacementClipboard(clipboard, expectedSequence))
            {
                return true;
            }
            if (!clipboard.Open())
            {
                return false;
            }
            try
            {
                if (_ownsReplacementClipboard(clipboard, expectedSequence) && !clipboard.Restore(backup))
                {
                    Trace.TraceWarning("替换后未能完整还原剪贴板。");
                }
                return true;
            }
            finally
            {
                clipboard.Close();
            }
        }
    }

    // 写入译文并记录是否清空过剪贴板，只有临时写入才添加隐私标记。
    private static (bool Written, bool Changed) _writeTranslation(string text, bool temporary)
    {
        bool changed = false;
        bool written = _writeUnicodeText(text, () => changed = _emptyClipboard(), _setClipboardData);
        if (written && temporary)
        {
            _tryMarkTemporaryClipboard();
        }
        return (written, changed);
    }

    // 在剪贴板仍打开时写入三个隐私格式，单个格式写入失败不阻断粘贴。
    private static void _tryMarkTemporaryClipboard()
    {
        const uint movableZeroInitialized = 0x42;
        foreach (string name in PrivacyFormats)
        {
            uint format = _registerClipboardFormat(name);
            if (format == 0)
            {
                continue;
            }
            nint memory = _globalAlloc(movableZeroInitialized, sizeof(uint));
            if (memory != 0 && _setClipboardData(format, memory) == 0)
            {
                _globalFree(memory);
            }
        }
    }

    // 关窗前和粘贴前均拒绝已关闭、不可见或最小化的来源。
    internal static bool _isReplaceTargetAvailable(nint sourceWindow) =>
        sourceWindow != 0 && _isWindow(sourceWindow) && _isWindowVisible(sourceWindow) && !_isIconic(sourceWindow);

    // 检查原生窗口句柄当前是否有效。
    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    private static extern bool _isWindow(nint window);

    // 判断来源是否已经最小化。
    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    private static extern bool _isIconic(nint window);

    // 在浮窗仍拥有激活权限时请求系统切回来源窗口。
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    internal static extern bool _setForegroundWindow(nint window);

    // 同步剪贴板操作在一次打开和关闭之间执行，便于验证失败及并发分支。
    internal sealed record ReplaceClipboardOperations(Func<bool> Open, Action Close,
        Func<CancellationToken, (ClipboardBackup? Backup, string? Error)> Snapshot,
        Func<bool, (bool Written, bool Changed)> Write, Func<bool> IsOwned, Func<uint> Sequence,
        Func<ClipboardBackup, bool> Restore, Action<ClipboardBackup> Free);
}
