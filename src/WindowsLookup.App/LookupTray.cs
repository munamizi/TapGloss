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
    private const string ShortcutLabel = "Alt+T";
    private WindowsHotkey? _hotkey;
    private readonly WindowsTray _icon;
    private LookupWindow? _window;
    private readonly SelectionReader _selection = new(Environment.ProcessPath);
    private readonly DeepSeekClient _translations = new();
    private readonly DeepSeekKeyStore _keys = new();
    private readonly SpeechModeStore _speechModeStore = new();
    private readonly AmericanSpeech _speech = new();
    private SpeechMemoryMode _speechMode;
    private Task _speechTask = Task.CompletedTask;
    private LookupResult? _queuedSpeech;
    private CancellationTokenSource? _speechRequest;
    private string? _apiKey;
    private string? _keyLoadError;
    private Task _lookupTask = Task.CompletedTask;
    private Task<SelectionCapture> _captureTask = Task.FromResult(new SelectionCapture(CaptureStatus.Unavailable));
    private CancellationTokenSource? _currentLookup;
    private (string Text, string Key, CancellationTokenSource Lifetime, Task<LookupResult> Task)? _pendingTranslation;
    private (nint Window, nint Focus, uint Sequence, long StartedAt)? _manualCopy;
    private bool _editingKey;
    private bool _exiting;
    private bool _disposed;
    private SettingsWindow? _settingsWindow;
    private LookupResult? _visibleResult;

    /// <summary>
    /// 创建托盘入口，读取本地配置并尝试注册快捷键。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     LookupTray - 管理查词和设置的托盘上下文，快捷键被占用时仍可打开设置。
    /// </summary>
    internal LookupTray()
    {
        string? shortcutError = null;
        try
        {
            _hotkey = new WindowsHotkey(HotkeyModifiers.Alt, Forms.Keys.T);
            _hotkey.Pressed += _queueLookup;
        }
        catch (Win32Exception ex)
        {
            shortcutError = _describeShortcutError(ex);
        }
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
        _icon = new WindowsTray(
            _hotkey is null ? "TapGloss · 快捷键未启用" : $"TapGloss · {ShortcutLabel}",
            async () => await _editKeyAsync(),
            () => _speechMode,
            _selectSpeechMode,
            async () => await _exitAsync());
        if (shortcutError is not null)
        {
            _icon._notify("快捷键未启用", shortcutError, warning: true);
        }

        // 启动后即在后台完成一次静默合成，托盘可立即使用。
        _ = _speech.PrepareAsync();
    }

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

    // 新请求先取消上一次，并等待可能还在进行的剪贴板恢复。
    private void _queueLookup(nint sourceWindow)
    {
        if (_exiting || _editingKey || _hotkey is null
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
            _hotkey.Handle, Forms.Cursor.Position, desktopId);
    }

    // 从本次来源取词，异步查询模型并更新浮窗。
    private async Task _runLookupAsync(CancellationTokenSource request, nint sourceWindow, nint sourceFocus,
        nint clipboardWindow, Point anchor, Guid desktopId)
    {
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
                && Environment.TickCount64 - pending.StartedAt < 60_000 ? pending.Sequence : 0;
            _captureTask = _captureLookupAsync(_captureTask, sourceWindow, clipboardWindow, token, previousManualSequence,
                sourceFocus);
            SelectionCapture capture = await _captureTask;
            token.ThrowIfCancellationRequested();
            selectionBounds = capture.SelectionBounds;
            if (capture.Status == CaptureStatus.NeedsManualCopy)
            {
                _cancelTranslation();
                _manualCopy = (sourceWindow, sourceFocus, capture.ClipboardSequence, Environment.TickCount64);
                string instructions = $"在原窗口按 Ctrl+C，再按 {ShortcutLabel}。取得文字后直接翻译。";
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
                window._display(anchor, "未取得文字", capture.Detail ?? $"请先选中英文，再按 {ShortcutLabel}。");
                return;
            }

            token.ThrowIfCancellationRequested();
            Task<LookupResult> translation = _translate(capture, key);
            if (!translation.IsCompleted)
            {
                window._display(anchor, "正在查词", capture.Text ?? "", selectionBounds: selectionBounds);

                // 预计会自动朗读单词时，在等待译文的时间里先加载本地音色。
                string? word = capture.Text?.Trim();
                if (word is { Length: > 0 and <= 80 } && !word.Any(char.IsWhiteSpace))
                {
                    _ = _speech.PrepareAsync();
                }
            }
            LookupResult result = await translation.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            _showResult(window, anchor, result, selectionBounds);
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
                    string message = ex is LookupException ? ex.Message : "本次查询未完成，请再按 Alt+T 重试。";
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
        nint sourceWindow, nint clipboardWindow, CancellationToken token, uint previousManualSequence, nint sourceFocus)
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
        return await _selection.CaptureAsync(sourceWindow, clipboardWindow, token, previousManualSequence, sourceFocus);
    }

    // 相同的已核验原文复用进行中的请求，其他原文发起请求前取消旧的网络等待。
    private Task<LookupResult> _translate(SelectionCapture capture, string key)
    {
        string text = capture.Text?.Trim() ?? "";
        if (_pendingTranslation is { } pending && pending.Text == text && pending.Key == key)
        {
            return pending.Task;
        }
        _cancelTranslation();

        var lifetime = new CancellationTokenSource();
        Task<LookupResult> task = _translations.TranslateAsync(capture, key, lifetime.Token);
        _pendingTranslation = (text, key, lifetime, task);
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

    // 单词显示词义和可用音标后自动朗读一次，短语和句子只展示译文。
    private void _showResult(LookupWindow window, Point anchor, LookupResult result, Rectangle? selectionBounds)
    {
        _visibleResult = result;
        if (!result.IsWord)
        {
            window._display(anchor, "译文", result.Chinese, selectionBounds: selectionBounds);
            return;
        }
        string body = result.AmericanIpa is { } ipa ? result.Chinese + Environment.NewLine + ipa : result.Chinese;
        window._display(anchor, result.Source, body,
            speak: () => _queueSpeech(result), selectionBounds: selectionBounds);
        _queueSpeech(result);
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
            Trace.TraceWarning("Amy 朗读结束（{0}）。", ex.GetType().Name);
            if (!_exiting && ReferenceEquals(_visibleResult, result) && _window is { IsVisible: true } window)
            {
                string message = ex switch
                {
                    FileNotFoundException => "Amy 语音文件不完整，请使用完整发布包。",
                    TimeoutException => "Amy 发音超时，可点击发音按钮重试。",
                    _ => "无法播放 Amy 发音，可点击发音按钮重试。",
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
        if (mode == SpeechMemoryMode.Resident)
        {
            _ = _speech.PrepareAsync();
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
        _manualCopy = null;
        _cancelTranslation();
        _currentLookup?.Cancel();
        _clearVisibleResult();
        _window?._hide();
        try
        {
            var key = new PasswordBox { Header = "DeepSeek API Key" };
            var dialog = new SettingsWindow("TapGloss · 设置 DeepSeek API Key", key, () => _saveKey(key.Password.Trim()));
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
        string message = _hotkey is null
            ? "API Key 已保存。请释放 Alt+T 后重启 TapGloss。"
            : $"API Key 已保存。选中文字后按 {ShortcutLabel}。";
        _icon._notify("TapGloss", message);
        return null;
    }

    // 根据 Windows 的注册错误给出固定热键的冲突提示。
    private static string _describeShortcutError(Win32Exception error) =>
        error.NativeErrorCode == 1409
            ? "Alt+T 已被其他程序占用。释放该组合后，重启 TapGloss。"
            : $"Windows 无法注册 Alt+T（错误 {error.NativeErrorCode}）。";

    // 释放当前组合的注册和消息窗口。
    private void _releaseHotkey()
    {
        if (_hotkey is not null)
        {
            _hotkey.Pressed -= _queueLookup;
            _hotkey.Dispose();
            _hotkey = null;
        }
    }

    // 等待进行中的取词释放剪贴板，再结束消息循环。
    private async Task _exitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        _icon.Visible = false;
        _clearVisibleResult();
        _window?._hide();
        _settingsWindow?.Close();
        _cancelTranslation();
        _currentLookup?.Cancel();
        try
        {
            await _captureTask;
            await _lookupTask;
            await _speechTask;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("退出时取词结束（{0}）。", ex.GetType().Name);
        }
        finally
        {
            Dispose();
            Application.Current.Exit();
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
        _releaseHotkey();
        _icon.Dispose();
        _speech.Dispose();
        _selection.Dispose();
        _window?._close();
    }
}
