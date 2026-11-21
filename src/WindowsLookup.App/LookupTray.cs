using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Forms = System.Windows.Forms;

namespace WindowsLookup.App;

/// <summary>管理托盘、选区确认、DeepSeek 查询和本地朗读。</summary>
internal sealed class LookupTray : IDisposable
{
    private const string ChineseShortcut = "Alt+T";
    private const string EnglishShortcut = "Alt+R";
    private readonly WindowsHotkey? _chineseHotkey;
    private readonly WindowsHotkey? _englishHotkey;
    private readonly WindowsTray _icon;
    private LookupWindow? _window;
    private readonly SelectionReader _selection = new(Environment.ProcessPath);
    private readonly DeepSeekClient _translations = new();
    private readonly DeepSeekKeyStore _keys = new();
    private readonly SpeechModeStore _speechModeStore = new();
    private readonly AmericanSpeech _speech = new(Environment.ProcessPath);
    private SpeechMemoryMode _speechMode;
    private Task _speechTask = Task.CompletedTask;
    private LookupResult? _queuedSpeech;
    private CancellationTokenSource? _speechRequest;
    private string? _apiKey;
    private string? _keyLoadError;
    private Task _lookupTask = Task.CompletedTask;
    private Task _replaceTask = Task.CompletedTask;
    private Task<SelectionCapture> _captureTask = Task.FromResult(new SelectionCapture(CaptureStatus.Unavailable));
    private CancellationTokenSource? _currentLookup;
    private (string Text, string Key, TranslationDirection Direction, CancellationTokenSource Lifetime, Task<LookupResult> Task)? _pendingTranslation;
    private (nint Window, nint Focus, TranslationDirection Direction, uint Sequence, long StartedAt)? _manualCopy;
    private bool _editingKey;
    private bool _exiting;
    private bool _disposed;
    private SettingsWindow? _settingsWindow;
    private LookupResult? _visibleResult;

    /// <summary>
    /// 创建托盘入口，读取本地配置并分别尝试注册两个翻译快捷键。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     LookupTray - 管理查词和设置的托盘上下文，快捷键被占用时仍可打开设置。
    /// </summary>
    internal LookupTray()
    {
        var shortcutErrors = new List<string>(2);
        _chineseHotkey = _tryRegisterHotkey(Forms.Keys.T, TranslationDirection.ToChinese, shortcutErrors);
        _englishHotkey = _tryRegisterHotkey(Forms.Keys.R, TranslationDirection.ToEnglish, shortcutErrors);
        try
        {
            _apiKey = _keys.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _keyLoadError = "之前保存的 API Key 无法读取，请重新设置。";
        }

        _speechMode = _speechModeStore._load();
        _speech.SetMemoryMode(_speechMode);
        string shortcutStatus = (_chineseHotkey is not null, _englishHotkey is not null) switch
        {
            (true, true) => $"{ChineseShortcut} / {EnglishShortcut}",
            (true, false) => $"{ChineseShortcut} · {EnglishShortcut} 未启用",
            (false, true) => $"{EnglishShortcut} · {ChineseShortcut} 未启用",
            _ => $"{ChineseShortcut} / {EnglishShortcut} 未启用",
        };
        _icon = new WindowsTray(
            $"TapGloss · {shortcutStatus}",
            () => _ = _runMenuActionAsync(_editKeyAsync),
            () => _speechMode,
            _selectSpeechMode,
            () => _ = _runMenuActionAsync(_exitAsync));
        if (shortcutErrors.Count > 0)
        {
            _icon._notify("快捷键未启用", string.Join(Environment.NewLine, shortcutErrors), warning: true);
        }

        // 模型包残缺时提示缺项，完整且热键可用时在后台预热。
        if (AmySpeechClient.ValidateVoiceBundle() is { } missingVoiceFile)
        {
            SpeechDiagnostics._record("bundle-check", SpeechFailureKind.BundleIncomplete);
            _icon._notify("Amy 语音文件不完整", $"缺少 {missingVoiceFile}，请使用完整发布包。", warning: true);
        }
        else if (_chineseHotkey is not null)
        {
            _ = _speech.PrepareAsync();
        }
    }

    // 一次只注册一个方向，冲突只影响该快捷键。
    private WindowsHotkey? _tryRegisterHotkey(Forms.Keys key, TranslationDirection direction, List<string> errors)
    {
        try
        {
            var hotkey = new WindowsHotkey(HotkeyModifiers.Alt, key);
            hotkey.Pressed += source => _queueLookup(source, direction);
            return hotkey;
        }
        catch (Win32Exception ex)
        {
            string shortcut = _shortcutLabel(direction);
            errors.Add(ex.NativeErrorCode == 1409
                ? $"{shortcut} 已被其他程序占用。释放该组合后，重启 TapGloss。"
                : $"Windows 无法注册 {shortcut}（错误 {ex.NativeErrorCode}）。");
            return null;
        }
    }

    // 取得本次翻译方向对应的快捷键名称。
    private static string _shortcutLabel(TranslationDirection direction) =>
        direction == TranslationDirection.ToEnglish ? EnglishShortcut : ChineseShortcut;

    // 在热键来源的桌面重用浮窗，换桌面或桌面归属不明确时创建新窗口。
    private LookupWindow _lookupWindowFor(Guid desktopId, nint sourceWindow)
    {
        LookupWindow? oldWindow = _window;
        if (oldWindow is not null && desktopId != Guid.Empty && desktopId == oldWindow.DesktopId)
        {
            oldWindow.SourceWindow = sourceWindow;
            return oldWindow;
        }

        var window = new LookupWindow(desktopId, sourceWindow);
        window.Dismissed += keepManualCopy =>
        {
            _clearVisibleResult();
            if (!keepManualCopy)
            {
                _manualCopy = null;
            }
            _cancelTranslation();
            _currentLookup?.Cancel();
        };
        _window = window;
        oldWindow?._close();
        return window;
    }

    // 新请求先取消上一次，本次取词单独读取前台来源。
    private void _queueLookup(nint sourceWindow, TranslationDirection direction)
    {
        nint clipboardWindow = (_chineseHotkey ?? _englishHotkey)?.Handle ?? 0;
        if (_exiting || _disposed || _editingKey || clipboardWindow == 0
            || _icon.IsMenuOpen || sourceWindow == _window?.Handle)
        {
            return;
        }
        _currentLookup?.Cancel();
        _clearVisibleResult();
        _window?._hide();
        var request = new CancellationTokenSource();
        _currentLookup = request;
        nint sourceFocus = WindowsHotkey.GetFocusedWindow(sourceWindow);
        Guid desktopId = VirtualDesktop._getDesktopId(sourceWindow);
        _lookupTask = _runLookupAsync(request, sourceWindow, sourceFocus,
            clipboardWindow, Forms.Cursor.Position, desktopId, direction);
    }

    // 从本次来源取词，异步查询模型并更新浮窗。
    private async Task _runLookupAsync(CancellationTokenSource request, nint sourceWindow, nint sourceFocus,
        nint clipboardWindow, Point anchor, Guid desktopId, TranslationDirection direction)
    {
        string shortcut = _shortcutLabel(direction);
        CancellationToken token = request.Token;
        LookupWindow? window = null;
        Rectangle? selectionBounds = null;
        try
        {
            token.ThrowIfCancellationRequested();
            window = _lookupWindowFor(desktopId, sourceWindow);
            string? key = _apiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                window._display(anchor, "需要 DeepSeek API Key",
                    _keyLoadError ?? "右键托盘图标，选择「设置 API Key」。");
                return;
            }

            var manualCopy = _manualCopy;
            _manualCopy = null;
            uint previousManualSequence = manualCopy is { } pending && pending.Window == sourceWindow && pending.Focus == sourceFocus
                && pending.Direction == direction && Environment.TickCount64 - pending.StartedAt < 60_000 ? pending.Sequence : 0;
            _captureTask = _captureLookupAsync(_captureTask, sourceWindow, clipboardWindow, token, previousManualSequence,
                sourceFocus, shortcut);
            SelectionCapture capture = await _captureTask;
            token.ThrowIfCancellationRequested();
            selectionBounds = capture.SelectionBounds;
            if (capture.Status == CaptureStatus.NeedsManualCopy)
            {
                _cancelTranslation();
                _manualCopy = (sourceWindow, sourceFocus, direction, capture.ClipboardSequence, Environment.TickCount64);
                string instructions = $"在原窗口按 Ctrl+C，再按 {shortcut}。取得文字后直接翻译。";
                if (!string.IsNullOrWhiteSpace(capture.Detail))
                {
                    instructions = capture.Detail + Environment.NewLine + instructions;
                }
                window._display(anchor, "请手动复制选中文字", instructions);
                return;
            }

            if (capture.Status is not (CaptureStatus.Selected or CaptureStatus.Copied))
            {
                _cancelTranslation();
                string selectionHint = direction == TranslationDirection.ToEnglish ? "请先选中文字" : "请先选中英文";
                window._display(anchor, "未取得文字", capture.Detail ?? $"{selectionHint}，再按 {shortcut}。");
                return;
            }

            token.ThrowIfCancellationRequested();
            Task<LookupResult> translation = _translate(capture, key, direction);
            if (!translation.IsCompleted)
            {
                window._display(anchor, "正在查词", capture.Text ?? "", selectionBounds: selectionBounds);

                // 网络请求进行时预合成同一个词，音频可供译文弹出后的朗读复用。
                if (direction == TranslationDirection.ToChinese
                    && DeepSeekClient.NormalizeWordCandidate(capture.Text) is { } word)
                {
                    _ = _speech.PrepareWordAsync(word);
                }
            }
            LookupResult result = await translation.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            nint verifiedFocus = capture.VerifiedFocus != 0 ? capture.VerifiedFocus : sourceFocus;
            _showResult(window, anchor, result, selectionBounds, clipboardWindow, sourceWindow, verifiedFocus,
                desktopId, capture.IsTerminal);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("查词任务失败（{0}）。", ex.GetType().Name);
            if (!token.IsCancellationRequested && window is not null)
            {
                _cancelTranslation();
                try
                {
                    string message = ex is LookupException ? ex.Message : $"本次查询未完成，请再按 {shortcut} 重试。";
                    window._display(anchor, "查词失败", message, selectionBounds: selectionBounds);
                }
                catch (Exception displayError)
                {
                    Trace.TraceWarning("查词提示显示失败（{0}）。", displayError.GetType().Name);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_currentLookup, request))
            {
                _currentLookup = null;
            }
            request.Dispose();
        }
    }

    // 只让取词串行，旧请求失败或取消后让下一次查询继续。
    private async Task<SelectionCapture> _captureLookupAsync(Task<SelectionCapture> previous,
        nint sourceWindow, nint clipboardWindow, CancellationToken token, uint previousManualSequence, nint sourceFocus,
        string shortcut)
    {
        try
        {
            await previous;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("上一笔取词未完成（{0}）。", ex.GetType().Name);
        }
        token.ThrowIfCancellationRequested();
        return await _selection.CaptureAsync(sourceWindow, clipboardWindow, token, previousManualSequence, sourceFocus, shortcut);
    }

    // 原文、方向和密钥都相同时复用进行中的请求。
    private Task<LookupResult> _translate(SelectionCapture capture, string key, TranslationDirection direction)
    {
        string text = capture.Text?.Trim() ?? "";
        if (_pendingTranslation is { } pending && pending.Text == text && pending.Key == key && pending.Direction == direction
            && !pending.Task.IsFaulted && !pending.Task.IsCanceled)
        {
            return pending.Task;
        }
        _cancelTranslation();

        var lifetime = new CancellationTokenSource();
        Task<LookupResult> task = _translations.TranslateAsync(capture, key, lifetime.Token, direction);
        _pendingTranslation = (text, key, direction, lifetime, task);
        _ = _releaseTranslationAsync(task, lifetime);
        return task;
    }

    // 观察被新查询替换的网络任务，并在完成后释放自己的取消令牌。
    private async Task _releaseTranslationAsync(Task<LookupResult> task, CancellationTokenSource lifetime)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning("翻译请求结束（{0}）。", ex.GetType().Name);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_pendingTranslation?.Lifetime, lifetime))
            {
                _pendingTranslation = null;
            }
            lifetime.Dispose();
        }
    }

    // 关窗、打开设置或切换到不同原文后停止旧的网络请求。
    private void _cancelTranslation()
    {
        if (_pendingTranslation is { } pending)
        {
            _pendingTranslation = null;
            pending.Lifetime.Cancel();
        }
    }

    // 英文译文可复制或替换原选区，英译中单词显示词义和可用音标并自动朗读。
    private void _showResult(LookupWindow window, Point anchor, LookupResult result, Rectangle? selectionBounds,
        nint clipboardWindow, nint sourceWindow, nint sourceFocus, Guid desktopId, bool isTerminal)
    {
        _visibleResult = result;
        if (result.Direction == TranslationDirection.ToEnglish)
        {
            window._display(anchor, "译文", result.Text, selectionBounds: selectionBounds,
                copy: token => ClipboardFallback.CopyTextAsync(clipboardWindow, result.Text, token),
                replace: token => _replaceResultAsync(window, result, sourceWindow, sourceFocus, clipboardWindow,
                    desktopId, isTerminal, token));
            return;
        }
        if (!result.IsWord)
        {
            window._display(anchor, "译文", result.Text, selectionBounds: selectionBounds);
            return;
        }
        string body = result.AmericanIpa is { } ipa ? result.Chinese + Environment.NewLine + ipa : result.Chinese;
        window._display(anchor, result.Source, body,
            speak: () => _queueSpeech(result), selectionBounds: selectionBounds);
        _queueSpeech(result);
    }

    // 把关窗前的结果交给浮窗，并跟踪完整任务，退出时等待剪贴板收尾。
    private Task<string?> _replaceResultAsync(LookupWindow window, LookupResult result, nint sourceWindow,
        nint sourceFocus, nint clipboardWindow, Guid desktopId, bool isTerminal, CancellationToken token)
    {
        if (_exiting || _disposed || !ReferenceEquals(_visibleResult, result))
        {
            return Task.FromResult<string?>(null);
        }
        if (!_replaceTask.IsCompleted)
        {
            return Task.FromResult<string?>("上一笔替换仍在处理，请稍后重试。");
        }
        const string desktopError = "原窗口已离开当前桌面，请使用复制按钮。";
        if (!_canReachSourceDesktop())
        {
            return Task.FromResult<string?>(desktopError);
        }

        var preHideResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = window.DispatcherQueue;
        bool hidden = false;
        string? switchError = null;
        _replaceTask = _completeReplacementAsync();
        return preHideResult.Task;

        // 桌面归属可用时查询 Shell，同时拦截被 Shell 遮蔽的窗口。
        bool _canReachSourceDesktop() =>
            !VirtualDesktop._isShellCloaked(sourceWindow)
            && (desktopId == Guid.Empty || VirtualDesktop._isCurrent(desktopId, sourceWindow));

        // 关窗后由托盘接收失败状态；关窗前由还在显示的卡片提示。
        async Task _completeReplacementAsync()
        {
            try
            {
                ReplaceResult outcome = await ClipboardFallback.ReplaceSelectionAsync(sourceWindow, sourceFocus,
                    clipboardWindow, result.Text, _switchToSourceAndHide, isTerminal, cancellationToken: token);
                if (!hidden)
                {
                    preHideResult.TrySetResult(switchError ?? outcome.Detail);
                }
                else if (!_exiting && outcome.Status != ReplaceStatus.Succeeded)
                {
                    string message = outcome.Detail ?? "未完成自动替换。";
                    if (outcome.LeftInClipboard)
                    {
                        message += "译文已留在剪贴板，可手动按 Ctrl+V。";
                    }
                    _icon._notify("TapGloss", message, warning: true);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !hidden)
            {
            }
            catch (Exception error)
            {
                Trace.TraceWarning("替换译文失败（{0}）。", error.GetType().Name);
                if (hidden && !_exiting)
                {
                    _icon._notify("TapGloss", "未完成自动替换，请检查原窗口和剪贴板。", warning: true);
                }
                else
                {
                    preHideResult.TrySetResult("未完成自动替换，请使用复制按钮。");
                }
            }
            finally
            {
                preHideResult.TrySetResult(null);
            }
        }

        // 在同一界面调度帧完成最后一次校验、交回前台以及关窗。
        Task<bool> _switchToSourceAndHide()
        {
            var switched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (token.IsCancellationRequested || _exiting || !ReferenceEquals(_window, window)
                        || !window.IsVisible || !ReferenceEquals(_visibleResult, result))
                    {
                        switched.SetResult(false);
                        return;
                    }
                    if (!ClipboardFallback._isReplaceTargetAvailable(sourceWindow))
                    {
                        switchError = "原窗口已关闭、最小化或不可见，请使用复制按钮。";
                        switched.SetResult(false);
                        return;
                    }
                    if (!_canReachSourceDesktop())
                    {
                        switchError = desktopError;
                        switched.SetResult(false);
                        return;
                    }

                    nint foreground = WindowsHotkey._getForegroundWindow();
                    if (foreground != sourceWindow && foreground != window.Handle)
                    {
                        switched.SetResult(false);
                        return;
                    }
                    if (foreground != sourceWindow)
                    {
                        ClipboardFallback._setForegroundWindow(sourceWindow);
                    }
                    window._dismiss(keepManualCopy: false);
                    hidden = true;
                    preHideResult.TrySetResult(null);
                    switched.SetResult(true);
                }
                catch (Exception error)
                {
                    switched.TrySetException(error);
                }
            }))
            {
                switched.SetResult(false);
            }
            return switched.Task;
        }
    }

    // 朗读同一结果时合并重复点击，播放结束后可再听一遍。
    private void _queueSpeech(LookupResult result)
    {
        if (!ReferenceEquals(_visibleResult, result) || ReferenceEquals(_queuedSpeech, result))
        {
            return;
        }
        _queuedSpeech = result;
        var request = new CancellationTokenSource();
        _speechRequest = request;
        _speechTask = _speakAsync(_speechTask, result, request);
    }

    // 等待音频设备空闲，开始前确认该结果仍显示在浮窗里。
    private async Task _speakAsync(Task previous, LookupResult result, CancellationTokenSource request)
    {
        try
        {
            await previous;
            if (_exiting || !ReferenceEquals(_visibleResult, result) || _window is not { IsVisible: true })
            {
                return;
            }
            await _speech.SpeakAsync(result.Source, request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var speech = ex as SpeechException;
            var kind = speech?.Kind ?? SpeechFailureKind.SynthesisFailed;
            SpeechDiagnostics._record("lookup-speech", kind, speech?.Code ?? ex.HResult);
            if (!_exiting && ReferenceEquals(_visibleResult, result) && _window is { IsVisible: true } window)
            {
                string message = kind switch
                {
                    SpeechFailureKind.BundleIncomplete => "Amy 语音文件不完整，请使用完整发布包。",
                    SpeechFailureKind.SynthesisTimeout => "Amy 发音超时，可点击发音按钮重试。",
                    SpeechFailureKind.AudioDeviceFailed => "无法访问音频输出设备，请检查扬声器或耳机后重试。",
                    _ => "Amy 语音引擎异常，可点击发音按钮重试（详情见日志）。",
                };
                try
                {
                    window._showNotice(message);
                }
                catch (Exception displayError)
                {
                    Trace.TraceWarning("发音提示显示失败（{0}）。", displayError.GetType().Name);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_speechRequest, request))
            {
                _speechRequest = null;
                _queuedSpeech = null;
            }
            request.Dispose();
        }
    }

    // 关闭浮窗或开始新查询时停止为旧结果生成及播放音频。
    private void _clearVisibleResult()
    {
        _visibleResult = null;
        _queuedSpeech = null;
        _speechRequest?.Cancel();
    }

    // 保存后立即应用模式，切到静态常驻时按需把 Amy 加载回来。
    private void _selectSpeechMode(SpeechMemoryMode mode)
    {
        if (_exiting || mode == _speechMode)
        {
            return;
        }
        try
        {
            _speechModeStore._save(mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _icon._notify("TapGloss", "无法保存语音模式，请检查本地配置目录权限。", warning: true);
            return;
        }
        _speechMode = mode;
        _speech.SetMemoryMode(mode);
        if (mode == SpeechMemoryMode.Resident && _chineseHotkey is not null)
        {
            _ = _speech.PrepareAsync();
        }
    }

    // 托盘命令由同步事件发起，异步异常在命令边界记录。
    private static async Task _runMenuActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("托盘命令失败（{0}）。", ex.GetType().Name);
        }
    }

    // 在本地更新加密保存的 Key。
    private async Task _editKeyAsync()
    {
        if (_editingKey || _exiting)
        {
            return;
        }
        _editingKey = true;
        try
        {
            _manualCopy = null;
            _cancelTranslation();
            _currentLookup?.Cancel();
            _clearVisibleResult();
            _window?._hide();
            var key = new PasswordBox { Header = "DeepSeek API Key" };
            var dialog = new SettingsWindow("TapGloss · 设置 DeepSeek API Key", key, () => _saveKey(key.Password));
            _settingsWindow = dialog;
            dialog._setSaveEnabled(false);
            key.PasswordChanged += (_, _) => dialog._setSaveEnabled(!string.IsNullOrWhiteSpace(key.Password));
            await dialog._showAsync(Forms.Cursor.Position, 184);
        }
        finally
        {
            _settingsWindow = null;
            _editingKey = false;
        }
    }

    // 保存密钥和更新状态，失败时把原因交给设置窗口。
    private string? _saveKey(string key)
    {
        if (DeepSeekClient.GetApiKeyValidationError(key) is { } keyError)
        {
            return keyError;
        }
        key = key.Trim();
        try
        {
            _keys.Save(key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return "无法保存 API Key，请检查本地配置目录权限。";
        }
        _apiKey = key;
        _keyLoadError = null;
        string message = (_chineseHotkey is not null, _englishHotkey is not null) switch
        {
            (true, true) => $"API Key 已保存。选中文字后按 {ChineseShortcut} 或 {EnglishShortcut}。",
            (true, false) => $"API Key 已保存。选中文字后按 {ChineseShortcut}。",
            (false, true) => $"API Key 已保存。选中文字后按 {EnglishShortcut}。",
            _ => "API Key 已保存。请释放快捷键后重启 TapGloss。",
        };
        _icon._notify("TapGloss", message);
        return null;
    }

    // 等待取词和译文替换收尾，再释放消息窗口并结束循环。
    private async Task _exitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        try
        {
            _cancelTranslation();
            _currentLookup?.Cancel();
            _clearVisibleResult();
            _window?._hide();
            _settingsWindow?.Close();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("准备退出失败（{0}）。", ex.GetType().Name);
        }
        try
        {
            await Task.WhenAll(_captureTask, _lookupTask, _speechTask, _replaceTask);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("退出时后台任务结束（{0}）。", ex.GetType().Name);
        }
        finally
        {
            try
            {
                await _selection.FinishClipboardAsync();
            }
            finally
            {
                try
                {
                    Dispose();
                }
                finally
                {
                    Application.Current.Exit();
                }
            }
        }
    }

    /// <summary>
    /// 释放托盘图标、菜单、浮窗、快捷键注册和本地语音进程。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _chineseHotkey?.Dispose();
        _englishHotkey?.Dispose();
        _icon.Dispose();
        _speech.Dispose();
        _selection.Dispose();
        _window?._close();
    }
}
