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
    public System.Drawing.Rectangle? SelectionBounds { get; internal init; }
}

/// <summary>按需读取热键触发时的前台选区。</summary>
public sealed class SelectionReader : IDisposable
{
    public const int MaxTextLength = 1000;
    private readonly SemaphoreSlim _captureGate = new(1);
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
    ///     3. 无法直接读取选区时，先尝试读取本次手动复制的新文本，再尝试临时复制。
    /// Args:
    ///     sourceWindow - 热键触发时记录的前台窗口句柄
    ///     clipboardWindow - 调用方持有的消息窗口句柄，用于恢复剪贴板
    ///     cancellationToken - 取消本次等待，已开始的临时复制在后台处理完剪贴板
    ///     previousManualSequence - 提示手动复制时的剪贴板序列号，0 表示没有待处理的手动复制
    ///     sourceFocus - 热键触发时的焦点控件句柄，0 表示进入读取时再获取。
    /// Returns:
    ///     SelectionCapture - 选中文字和可用的位置，或相应的失败状态
    /// </summary>
    public async Task<SelectionCapture> CaptureAsync(nint sourceWindow, nint clipboardWindow,
        CancellationToken cancellationToken = default, uint previousManualSequence = 0, nint sourceFocus = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isCurrentSource(sourceWindow, sourceFocus))
        {
            return new(CaptureStatus.Unavailable, Detail: "来源窗口已切换。");
        }
        sourceFocus = sourceFocus == 0 ? WindowsHotkey.GetFocusedWindow(sourceWindow) : sourceFocus;
        if (_isNativePassword(sourceFocus))
        {
            return new(CaptureStatus.Protected, Detail: "此控件包含受保护文本。");
        }
        if (!_captureGate.Wait(0))
        {
            return new(CaptureStatus.Unavailable, Detail: "正在读取上一处选区。");
        }

        try
        {
            SelectionCapture capture = _worker is null
                ? await _readInProcessAsync(sourceWindow, cancellationToken).ConfigureAwait(false)
                : await _worker._readAsync(sourceWindow, cancellationToken).ConfigureAwait(false);
            if (!_isCurrentSource(sourceWindow, sourceFocus))
            {
                return new(CaptureStatus.Unavailable, Detail: "读取时来源窗口已切换。");
            }
            if (capture.Status != CaptureStatus.Unavailable || capture.FallbackMode == ClipboardFallbackMode.None)
            {
                return capture;
            }

            SelectionCapture fallback = await ClipboardFallback._withClipboardBudgetAsync(
                token => previousManualSequence != 0
                    ? Task.FromResult(ClipboardFallback._readManualCopy(
                        sourceWindow, clipboardWindow, previousManualSequence, sourceFocus))
                    : ClipboardFallback._captureAsync(sourceWindow, clipboardWindow, token, sourceFocus),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return fallback;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    // 在取词和恢复之间核对前台窗口及可取得的原生焦点。
    internal static bool _isCurrentSource(nint sourceWindow, nint sourceFocus) =>
        sourceWindow != 0 && sourceWindow == WindowsHotkey._getForegroundWindow()
        && (sourceFocus == 0 || WindowsHotkey.GetFocusedWindow(sourceWindow) == sourceFocus);

    // UIA 不可用时仍拦截带密码样式的原生编辑框和富文本框。
    internal static bool _isNativePassword(nint focus)
    {
        const int style = -16;
        const int password = 0x20;
        if (focus == 0 || (_getWindowStyle(focus, style) & password) == 0)
        {
            return false;
        }

        var className = new StringBuilder(64);
        _getClassName(focus, className, className.Capacity);
        string name = className.ToString();
        return name.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int _getWindowStyle(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int _getClassName(nint window, StringBuilder className, int count);

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

    // 只沿焦点控件向上找文本控件，避免遍历整个窗口并读到其他位置的选区。
    internal static SelectionCapture _readSelection(nint sourceWindow)
    {
        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            AutomationElement? element = focused;
            for (int depth = 0; element is not null && depth < 12; depth++)
            {
                if (element.Current.IsPassword)
                {
                    return new(CaptureStatus.Protected, Detail: "此控件包含受保护文本。");
                }
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)
                    && pattern is TextPattern textPattern
                    && textPattern.SupportedTextSelection != SupportedTextSelection.None)
                {
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
                    var bounds = text.Length > 0 && lastRange is not null ? _readSelectionBounds(lastRange) : null;
                    if (sourceWindow != WindowsHotkey._getForegroundWindow()
                        || !Equals(focused, AutomationElement.FocusedElement))
                    {
                        return new(CaptureStatus.Unavailable, Detail: "读取时输入焦点已切换。");
                    }
                    return text.Length == 0
                        ? new(CaptureStatus.NoSelection)
                        : new(CaptureStatus.Selected, text) { SelectionBounds = bounds };
                }
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
            return _automationUnavailable(null);
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
