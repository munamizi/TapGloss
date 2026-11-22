using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;

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

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsTerminal { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal nint VerifiedFocus { get; init; }
}

/// <summary>按需读取热键触发时的前台选区。</summary>
public sealed class SelectionReader : IDisposable
{
    public const int MaxTextLength = 1000;
    private const int ControlTypeProperty = 30003;
    private const int NameProperty = 30005;
    private const int ClassNameProperty = 30012;
    private const int IsPasswordProperty = 30019;
    private const int IsTextPatternAvailableProperty = 30040;
    private const int TextPatternId = 10014;
    private const int EditControlType = 50004;
    private const int ElementNotAvailable = unchecked((int)0x80040201);
    private static readonly Lazy<(IUIAutomation2 Client, IUIAutomationCacheRequest Cache, IUIAutomationTreeWalker Walker)>
        Automation = new(_createAutomation);
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
    ///     SelectionCapture - 选中文字、可用的位置和最后核验的焦点，或相应的失败状态。
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
                return capture.Status == CaptureStatus.Selected ? capture with { VerifiedFocus = readFocus } : capture;
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
            return fallback.Status is CaptureStatus.Selected or CaptureStatus.Copied
                ? fallback with { VerifiedFocus = fallback.VerifiedFocus != 0 ? fallback.VerifiedFocus : readFocus }
                : fallback;
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

    // 选区读取请求共用 UIA 子进程，取消收尾时也保持请求串行。
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

    // 在 MTA 中共用自动化客户端、缓存请求和祖先遍历器。
    private static (IUIAutomation2, IUIAutomationCacheRequest, IUIAutomationTreeWalker) _createAutomation()
    {
        var client = (IUIAutomation2)new CUIAutomation8();
        client.ConnectionTimeout = 500;
        client.TransactionTimeout = 800;
        IUIAutomationCacheRequest cache = client.CreateCacheRequest();
        cache.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        cache.TreeScope = TreeScope.TreeScope_Element;
        cache.AddProperty(ControlTypeProperty);
        cache.AddProperty(NameProperty);
        cache.AddProperty(ClassNameProperty);
        cache.AddProperty(IsPasswordProperty);
        cache.AddProperty(IsTextPatternAvailableProperty);
        cache.AddPattern(TextPatternId);
        return (client, cache, client.ControlViewWalker);
    }

    // 先加载 UIA 并访问桌面根元素，把取词预算留给来源控件。
    internal static void _prepareAutomation()
    {
        try
        {
            _releaseAutomationObject(Automation.Value.Client.GetRootElement());
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            // 桌面暂不可读时仍发出就绪信号，随后按实际控件处理。
        }
    }

    // 批量读取焦点与祖先缓存，只在使用期内持有本次的 COM 代理。
    internal static SelectionCapture _readSelection(nint sourceWindow)
    {
        var elements = new List<IUIAutomationElement>();
        try
        {
            var (client, cache, walker) = Automation.Value;
            IUIAutomationElement? focused = _track(client.GetFocusedElementBuildCache(cache));
            if (focused is null)
            {
                return _automationUnavailable(null);
            }
            bool numberedTerminalName = ClipboardFallback._isVsCodeEditor(sourceWindow);
            return _scanFocusChain(focused, element => _track(walker.GetParentElementBuildCache(element, cache)),
                (element, depth) => _isAutomationTerminal(element, depth == 0, numberedTerminalName),
                _readAutomationElement, () =>
                {
                    if (sourceWindow != WindowsHotkey._getForegroundWindow())
                    {
                        return false;
                    }
                    IUIAutomationElement? current = _track(client.GetFocusedElement());
                    return current is not null && client.CompareElements(focused, current) != 0;
                });
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return _automationUnavailable(_automationErrorDetail(error));
        }
        finally
        {
            for (int index = elements.Count - 1; index >= 0; index--)
            {
                _releaseAutomationObject(elements[index]);
            }
        }

        // 记录每次从 COM 接口取得的引用，供读取结束时逐一释放。
        IUIAutomationElement? _track(IUIAutomationElement? element)
        {
            if (element is not null)
            {
                elements.Add(element);
            }
            return element;
        }
    }

    // UIA 统一通过 COM 错误码区分控件失效与接口不可访问。
    private static string _automationErrorDetail(Exception error) => error.HResult == ElementNotAvailable
        ? "来源控件已关闭或切换。" : "来源应用的选区接口不可访问。";

    // 只释放本次 UIA 调用取得的 COM 对象引用。
    private static void _releaseAutomationObject(object? value)
    {
        if (value is not null)
        {
            Marshal.ReleaseComObject(value);
        }
    }

    // 即使已取得选区，也检查剩余祖先以识别内嵌终端。
    internal static SelectionCapture _scanFocusChain<T>(T focused, Func<T, T?> getParent,
        Func<T, int, bool> isTerminal, Func<T, bool, SelectionCapture?> readElement, Func<bool> isStillFocused)
        where T : class
    {
        bool terminal = false;
        bool emptySelection = false;
        bool selectionRead = false;
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
                        selectionRead = true;
                        try
                        {
                            for (int ancestorDepth = depth + 1; !terminal && ancestorDepth < 12; ancestorDepth++)
                            {
                                element = getParent(element!);
                                if (element is null)
                                {
                                    break;
                                }
                                terminal = isTerminal(element, ancestorDepth);
                            }
                        }
                        catch (Exception ex) when (ex is COMException or InvalidOperationException)
                        {
                            // 已读取选区时保留文本，粘贴前仍会检查当时可访问的焦点与祖先。
                        }
                        return isStillFocused() ? capture! with { IsTerminal = terminal } : _automationFocusChanged();
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
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            if (terminal)
            {
                return _terminalNeedsSelection();
            }
            string detail = _automationErrorDetail(ex);
            return emptySelection || selectionRead ? new(CaptureStatus.Unavailable, Detail: detail) : _automationUnavailable(detail);
        }
    }

    // 先核对密码属性，再通过缓存的 TextPattern 读取选区文本和位置。
    private static SelectionCapture? _readAutomationElement(IUIAutomationElement element, bool readText)
    {
        if (element.CachedIsPassword != 0)
        {
            return new(CaptureStatus.Protected, Detail: "此控件包含受保护文本。");
        }
        if (!readText || element.GetCachedPropertyValue(IsTextPatternAvailableProperty) is not true)
        {
            return null;
        }

        object? pattern = element.GetCachedPattern(TextPatternId);
        IUIAutomationTextRangeArray? selection = null;
        try
        {
            if (pattern is not IUIAutomationTextPattern textPattern
                || textPattern.SupportedTextSelection == SupportedTextSelection.SupportedTextSelection_None)
            {
                return null;
            }
            selection = textPattern.GetSelection();
            if (selection is null)
            {
                return new(CaptureStatus.NoSelection);
            }
            return _readRanges(selection);
        }
        finally
        {
            _releaseAutomationObject(selection);
            _releaseAutomationObject(pattern);
        }
    }

    // 按原顺序拼接选区，保留最后一个含文字的范围用于浮窗定位。
    private static SelectionCapture _readRanges(IUIAutomationTextRangeArray selection)
    {
        var selected = new StringBuilder();
        IUIAutomationTextRange? lastRange = null;
        try
        {
            int length = selection.Length;
            for (int index = 0; index < length; index++)
            {
                IUIAutomationTextRange? range = selection.GetElement(index);
                try
                {
                    string part = range?.GetText(MaxTextLength + 1) ?? "";
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
                    _releaseAutomationObject(lastRange);
                    lastRange = range;
                    range = null;
                }
                finally
                {
                    _releaseAutomationObject(range);
                }
            }
            string text = selected.ToString().Trim();
            return text.Length == 0 ? new(CaptureStatus.NoSelection)
                : new(CaptureStatus.Selected, text) { SelectionBounds = _readSelectionBounds(lastRange!) };
        }
        finally
        {
            _releaseAutomationObject(lastRange);
        }
    }

    // 只在焦点是输入控件时读取终端标签，祖先按组件类名识别。
    private static bool _isAutomationTerminal(IUIAutomationElement element, bool focused, bool numberedTerminalName)
    {
        bool isEdit = focused && element.CachedControlType == EditControlType;
        return _matchesTerminalControl(element.CachedClassName ?? "", isEdit ? element.CachedName ?? "" : "",
            isEdit, numberedTerminalName);
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

    // 无法定位时仍保留已读取的文字。
    private static System.Drawing.Rectangle? _readSelectionBounds(IUIAutomationTextRange range)
    {
        try
        {
            return _lastVisibleBounds(range.GetBoundingRectangles());
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    // 每四个值构成一个物理像素矩形，从最后一条有效可见行开始查找。
    internal static System.Drawing.Rectangle? _lastVisibleBounds(double[]? rectangles)
    {
        if (rectangles is null || rectangles.Length < 4 || rectangles.Length % 4 != 0)
        {
            return null;
        }

        for (int index = rectangles.Length - 4; index >= 0; index -= 4)
        {
            double left = Math.Floor(rectangles[index]);
            double top = Math.Floor(rectangles[index + 1]);
            double width = rectangles[index + 2];
            double height = rectangles[index + 3];
            double right = Math.Ceiling(rectangles[index] + width);
            double bottom = Math.Ceiling(rectangles[index + 1] + height);
            if (width > 0 && height > 0 && left >= int.MinValue && top >= int.MinValue
                && right <= int.MaxValue && bottom <= int.MaxValue
                && right > left && bottom > top && right - left <= int.MaxValue && bottom - top <= int.MaxValue)
            {
                return System.Drawing.Rectangle.FromLTRB((int)left, (int)top, (int)right, (int)bottom);
            }
        }
        return null;
    }
}
