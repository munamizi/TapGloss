using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace WindowsLookup;

public enum CaptureStatus
{
    Selected,
    Copied,
    NoSelection,
    Unavailable,
    Protected,
    TooLong,
    NeedsManualCopy,
}

internal enum ClipboardFallbackMode
{
    None,
    Automatic,
}

/// <summary>成功读取选区或新复制文本时携带文字，其余状态可携带本地提示。</summary>
public sealed record SelectionCapture(CaptureStatus Status, string? Text = null, string? Detail = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public uint ClipboardSequence { get; internal init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal ClipboardFallbackMode FallbackMode { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool AutomationFocusChecked { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Drawing.Rectangle? SelectionBounds { get; internal init; }
}

/// <summary>按需读取热键触发时的前台选区。</summary>
public sealed class SelectionReader : IDisposable
{
    public const int MaxTextLength = 1000;
    private readonly SemaphoreSlim _captureGate = new(1);
    private readonly SemaphoreSlim _automationGate = new(1);
    private readonly SelectionAutomationWorker? _worker;
    private Task<SelectionCapture>? _automationRead;

    /// <summary>
    /// 创建前台选区读取器，可在独立进程执行 Windows 自动化调用。
    ///
    /// Args:
    ///     workerExecutable - 支持选区读取模式的程序路径，省略时在本进程调用。
    /// Returns:
    ///     SelectionReader - 可执行选区读取的实例。
    /// </summary>
    public SelectionReader(string? workerExecutable = null)
    {
        if (!string.IsNullOrWhiteSpace(workerExecutable))
        {
            _worker = new SelectionAutomationWorker(workerExecutable);
        }
    }

    /// <summary>
    /// 在选区读取子进程中处理父进程发送的窗口句柄。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值，父进程关闭输入后结束。
    /// </summary>
    public static void RunWorker() => SelectionAutomationWorker._respond();

    /// <summary>
    /// 关闭尚在运行的选区读取子进程。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值。
    /// </summary>
    public void Dispose() => _worker?.Dispose();

    /// <summary>
    /// 等待已开始的剪贴板操作收尾，供关闭消息窗口前调用。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值，后台操作关闭剪贴板后任务完成。
    /// </summary>
    public Task FinishClipboardAsync() => ClipboardFallback._waitForIdleAsync();

    /// <summary>
    /// 读取前台选区，必要时尝试一次临时复制。
    ///
    /// 步骤：
    ///     1. 确认来源仍在前台，并限制同时运行的读取。
    ///     2. 在后台读取 UI Automation 选区文字及可用的屏幕位置。
    ///     3. 在允许降级时核对本次手动复制的新文本，或为合适的来源临时复制。
    /// Args:
    ///     sourceWindow - 热键触发时记录的前台窗口句柄
    ///     clipboardWindow - 调用方持有的消息窗口句柄，用于恢复剪贴板
    ///     cancellationToken - 取消本次等待，已开始的临时复制在后台处理完剪贴板
    ///     previousManualSequence - 提示手动复制时的剪贴板序列号，0 表示没有待处理的手动复制
    ///     sourceFocus - 热键触发时的焦点控件句柄，0 表示进入读取时再获取。
    ///     shortcutLabel - 剪贴板超时时提示的快捷键，默认 Alt+T
    /// Returns:
    ///     SelectionCapture - 选中文字和可用的位置，或相应的失败状态
    /// </summary>
    public async Task<SelectionCapture> CaptureAsync(nint sourceWindow, nint clipboardWindow,
        CancellationToken cancellationToken = default, uint previousManualSequence = 0, nint sourceFocus = 0,
        string shortcutLabel = "Alt+T")
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isCurrentSource(sourceWindow, 0))
        {
            return new(CaptureStatus.Unavailable, Detail: "来源窗口已切换。");
        }
        nint readFocus = WindowsHotkey.GetFocusedWindow(sourceWindow);
        bool focusMoved = sourceFocus != 0 && readFocus != sourceFocus;
        if (focusMoved && (previousManualSequence != 0 || !ClipboardFallback._isFocusInSource(sourceWindow, readFocus)))
        {
            return new(CaptureStatus.Unavailable, Detail: "来源窗口内的输入焦点已切换。");
        }
        if (_isNativePassword(readFocus))
        {
            return new(CaptureStatus.Protected, Detail: "此控件包含受保护文本。");
        }
        if (!_captureGate.Wait(0))
        {
            return new(CaptureStatus.Unavailable, Detail: "正在读取上一处选区。");
        }

        try
        {
            SelectionCapture capture = await _readAutomationAsync(sourceWindow, cancellationToken).ConfigureAwait(false);
            if (!_isCurrentSource(sourceWindow, 0))
            {
                return new(CaptureStatus.Unavailable, Detail: "来源窗口已切换。");
            }

            bool quickProbe = capture.Status == CaptureStatus.NoSelection
                && (previousManualSequence != 0 || ClipboardFallback._canProbeNoSelection(sourceWindow));
            bool useClipboard = quickProbe || capture.FallbackMode == ClipboardFallbackMode.Automatic;
            if ((!useClipboard || previousManualSequence != 0) && !_isCurrentSource(sourceWindow, readFocus, strictFocus: true))
            {
                return new(CaptureStatus.Unavailable, Detail: "读取时输入焦点已切换。");
            }
            if (!useClipboard)
            {
                return capture;
            }
            if (focusMoved && !capture.AutomationFocusChecked)
            {
                return ClipboardFallback._unverifiedFocus();
            }

            SelectionCapture fallback = await ClipboardFallback._withClipboardBudgetAsync(
                token => previousManualSequence != 0
                    ? Task.FromResult(ClipboardFallback._readManualCopy(
                        sourceWindow, clipboardWindow, previousManualSequence, readFocus))
                    : ClipboardFallback._captureAsync(sourceWindow, clipboardWindow, token, readFocus, quickProbe,
                        recheckFocus: retryToken => _readAutomationAsync(sourceWindow, retryToken)),
                cancellationToken, shortcutLabel).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return fallback;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    // 在取词和恢复之间核对前台窗口及可取得的原生焦点。
    internal static bool _isCurrentSource(nint sourceWindow, nint sourceFocus, bool strictFocus = false) =>
        sourceWindow != 0 && sourceWindow == WindowsHotkey._getForegroundWindow()
        && (!strictFocus && sourceFocus == 0
            || WindowsHotkey._tryGetFocusState(sourceWindow, out nint focus, out _) && focus == sourceFocus);

    // UIA 不可用时仍拦截带密码样式的原生编辑框和富文本框。
    internal static bool _isNativePassword(nint focus)
    {
        const int style = -16;
        const int password = 0x20;
        if (focus == 0 || (_getWindowStyle(focus, style) & password) == 0)
        {
            return false;
        }

        string name = _getWindowClass(focus);
        return name.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    // 查询原生窗口类名，句柄无效时返回空字符串。
    internal static string _getWindowClass(nint window)
    {
        var className = new StringBuilder(128);
        return window != 0 && _getClassName(window, className, className.Capacity) != 0 ? className.ToString() : "";
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int _getWindowStyle(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int _getClassName(nint window, StringBuilder className, int count);

    // 初次读取和复制前的焦点复查共用 UIA 子进程，取消收尾时也保持请求串行。
    private async Task<SelectionCapture> _readAutomationAsync(nint sourceWindow, CancellationToken token)
    {
        await _automationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return _worker is null
                ? await _readInProcessAsync(sourceWindow, token).ConfigureAwait(false)
                : await _worker._readAsync(sourceWindow, token).ConfigureAwait(false);
        }
        finally
        {
            _automationGate.Release();
        }
    }

    // 兼容显式选择在本进程读取的调用方，限制同时挂起的 Windows 调用数量。
    private async Task<SelectionCapture> _readInProcessAsync(nint sourceWindow, CancellationToken token)
    {
        if (_automationRead is { IsCompleted: false })
        {
            return _automationUnavailable("来源应用的选区接口仍未响应。");
        }
        _automationRead = Task.Run(() => _readSelection(sourceWindow));
        try
        {
            return await _automationRead.WaitAsync(TimeSpan.FromMilliseconds(900), token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return _automationUnavailable("来源应用的选区接口响应超时。");
        }
    }

    // 让 UIA 在子进程发出就绪信号前加载，单次预算留给实际的来源控件读取。
    internal static void _prepareAutomation()
    {
        try
        {
            _ = AutomationElement.RootElement;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            // 桌面暂不可读时仍可送出就绪信号，随后按实际控件的读取结果处理。
        }
    }

    // 只沿焦点控件向上找文本控件，避免读到窗口其他位置的选区。
    internal static SelectionCapture _readSelection(nint sourceWindow)
    {
        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return _automationUnavailable(null);
            }

            bool numberedTerminalName = ClipboardFallback._isVsCodeEditor(sourceWindow);
            return _scanFocusChain(focused, element => TreeWalker.ControlViewWalker.GetParent(element),
                (element, depth) => _isAutomationTerminal(element, depth == 0, numberedTerminalName),
                _readAutomationElement, () => sourceWindow == WindowsHotkey._getForegroundWindow()
                    && Equals(focused, AutomationElement.FocusedElement));
        }
        catch (ElementNotAvailableException)
        {
            return _automationUnavailable("来源控件已关闭或切换。");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return _automationUnavailable("来源应用的选区接口不可访问。");
        }
    }

    // 首个文本控件没有选区时仍检查剩余祖先，避免向内嵌终端自动发 Ctrl+C。
    internal static SelectionCapture _scanFocusChain<T>(T focused, Func<T, T?> getParent,
        Func<T, int, bool> isTerminal, Func<T, bool, SelectionCapture?> readElement, Func<bool> isStillFocused)
        where T : class
    {
        bool terminal = false;
        bool emptySelection = false;
        try
        {
            T? element = focused;
            for (int depth = 0; element is not null && depth < 12; depth++)
            {
                terminal |= isTerminal(element, depth);
                if (terminal && emptySelection)
                {
                    return _terminalNeedsSelection();
                }

                SelectionCapture? capture = readElement(element, !emptySelection);
                switch (capture?.Status)
                {
                    case CaptureStatus.NoSelection:
                        if (terminal)
                        {
                            return _terminalNeedsSelection();
                        }
                        emptySelection = true;
                        break;
                    case CaptureStatus.Selected:
                        return isStillFocused() ? capture! : _automationFocusChanged();
                    case not null:
                        return capture!;
                }
                if (depth < 11)
                {
                    element = getParent(element);
                }
            }

            if (terminal)
            {
                return _terminalNeedsSelection();
            }
            if (!isStillFocused())
            {
                return _automationFocusChanged();
            }
            return emptySelection
                ? new(CaptureStatus.NoSelection) { AutomationFocusChecked = true }
                : _automationUnavailable(null) with { AutomationFocusChecked = true };
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            if (terminal)
            {
                return _terminalNeedsSelection();
            }
            string detail = ex is ElementNotAvailableException
                ? "来源控件已关闭或切换。" : "来源应用的选区接口不可访问。";
            return emptySelection ? new(CaptureStatus.Unavailable, Detail: detail) : _automationUnavailable(detail);
        }
    }

    // 先检查受保护控件，再读取首个可用文本控件的选区和末尾位置。
    private static SelectionCapture? _readAutomationElement(AutomationElement element, bool readText)
    {
        if (element.Current.IsPassword)
        {
            return new(CaptureStatus.Protected, Detail: "此控件包含受保护文本。");
        }
        if (!readText || !element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)
            || pattern is not TextPattern textPattern
            || textPattern.SupportedTextSelection == SupportedTextSelection.None)
        {
            return null;
        }

        var selected = new StringBuilder();
        TextPatternRange? lastRange = null;
        foreach (var range in textPattern.GetSelection())
        {
            string part = range.GetText(MaxTextLength + 1);
            if (part.Length == 0)
            {
                continue;
            }
            if (selected.Length > 0)
            {
                selected.Append('\n');
            }
            if (selected.Length + part.Length > MaxTextLength)
            {
                return new(CaptureStatus.TooLong, Detail: "一次最多处理 1000 个字符。");
            }
            selected.Append(part);
            lastRange = range;
        }
        string text = selected.ToString().Trim();
        return text.Length == 0 ? new(CaptureStatus.NoSelection)
            : new(CaptureStatus.Selected, text) { SelectionBounds = _readSelectionBounds(lastRange!) };
    }

    // 仅在焦点是输入控件时读取终端标签，祖先通过已知的组件类名识别。
    private static bool _isAutomationTerminal(AutomationElement element, bool focused, bool numberedTerminalName)
    {
        var properties = element.Current;
        bool isEdit = focused && properties.ControlType == ControlType.Edit;
        return _matchesTerminalControl(properties.ClassName, isEdit ? properties.Name : "", isEdit, numberedTerminalName);
    }

    // 匹配终端组件类和输入框标签，编号形式只用于已知的 VS Code 系进程。
    internal static bool _matchesTerminalControl(string className, string focusedName, bool isEdit, bool numberedTerminalName) =>
        className.Equals("xterm", StringComparison.OrdinalIgnoreCase)
        || className.StartsWith("xterm-", StringComparison.OrdinalIgnoreCase)
        || className.StartsWith("xterm ", StringComparison.OrdinalIgnoreCase)
        || className.Equals("JediTermPanel", StringComparison.OrdinalIgnoreCase)
        || isEdit && (focusedName.Equals("Terminal input", StringComparison.OrdinalIgnoreCase)
            || focusedName == "终端输入" || numberedTerminalName && _isNumberedTerminalLabel(focusedName));

    // VS Code 用终端编号开头，可追加标题或无障碍说明。
    private static bool _isNumberedTerminalLabel(string name)
    {
        int index = name.StartsWith("Terminal ", StringComparison.OrdinalIgnoreCase) ? "Terminal ".Length
            : name.StartsWith("终端", StringComparison.Ordinal) ? "终端".Length : 0;
        if (index == 0)
        {
            return false;
        }
        while (index < name.Length && name[index] == ' ')
        {
            index++;
        }
        int firstDigit = index;
        while (index < name.Length && char.IsAsciiDigit(name[index]))
        {
            index++;
        }
        return index > firstDigit && (index == name.Length || name[index] is ',' or '，' or '\r' or '\n'
            || name.AsSpan(index).StartsWith(" environment is stale,", StringComparison.Ordinal)
            || name.AsSpan(index).StartsWith(" 环境已过时，", StringComparison.Ordinal));
    }

    // UIA 焦点变化时丢弃原控件结果。
    private static SelectionCapture _automationFocusChanged() =>
        new(CaptureStatus.Unavailable, Detail: "读取时输入焦点已切换。");

    // 终端没有 UIA 选区时停止，不发送可能中断命令的 Ctrl+C。
    internal static SelectionCapture _terminalNeedsSelection() =>
        new(CaptureStatus.Unavailable, Detail: "终端没有提供可读取的选区。为避免中断命令，已停止自动复制。");

    // UIA 无法取得结果时交给剪贴板，取词入口会再次核对来源，复制前会复查原生密码框。
    internal static SelectionCapture _automationUnavailable(string? detail) =>
        new(CaptureStatus.Unavailable, Detail: detail) { FallbackMode = ClipboardFallbackMode.Automatic };

    // 取选区最后一条可见行的物理像素边界，供浮窗贴近选区末尾。
    private static System.Drawing.Rectangle? _readSelectionBounds(TextPatternRange range)
    {
        try
        {
            var rectangles = range.GetBoundingRectangles();
            for (int index = rectangles.Length - 1; index >= 0; index--)
            {
                var bounds = rectangles[index];
                if (bounds.Width > 0 && bounds.Height > 0
                    && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
                    && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom))
                {
                    return System.Drawing.Rectangle.FromLTRB(
                        (int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                        (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
                }
            }
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
        }
        return null;
    }
}
