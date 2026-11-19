using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WindowsLookup.App;

/// <summary>以 WinUI 卡片样式展示译文、单词音标及查词状态。</summary>
internal sealed partial class LookupWindow : Window
{
    private const int ExtendedStyle = -20;
    private const int ToolWindowStyle = 0x80;
    private const int AppWindowStyle = 0x40000;
    private const int DisableDwmTransitions = 3;
    private readonly OutsideClickWatcher _outsideClicks;
    private readonly DispatcherQueueTimer? _desktopWatcher;
    private readonly TextBlock _heading = new() { IsTextSelectionEnabled = true };
    private readonly TextBlock _body = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private readonly Button _speak = new();
    private readonly TextBlock _notice = new() { Visibility = Visibility.Collapsed };
    private Action? _speakAction;
    private Point _lastAnchor;
    private Rectangle? _lastSelectionBounds;
    private int _lastWidth = MinCardWidth;
    private int _lastHeight;
    private int _clientHeightOffset;
    private bool _hasLoadedOnce;
    private bool _closing;

    /// <summary>
    /// 创建无任务栏按钮的浮窗，并跟踪它应该显示的虚拟桌面。
    ///
    /// Args:
    ///     desktopId - 触发查词时来源窗口所属的虚拟桌面。
    ///     sourceWindow - 热键触发时的前台窗口句柄。
    /// Returns:
    ///     LookupWindow - 可重复显示的查词浮窗。
    /// </summary>
    internal LookupWindow(Guid desktopId, nint sourceWindow)
    {
        DesktopId = desktopId;
        Title = "TapGloss";
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        _excludeFromTaskbar();
        _disableWindowTransitions();
        _outsideClicks = new OutsideClickWatcher(DispatcherQueue, Handle,
            target => _dismissFromUi(keepManualCopy: target != 0 && target == SourceWindow));
        SourceWindow = sourceWindow;
        if (desktopId != Guid.Empty)
        {
            _desktopWatcher = DispatcherQueue.CreateTimer();
            _desktopWatcher.Interval = TimeSpan.FromMilliseconds(100);
            _desktopWatcher.Tick += (_, _) => _updateDesktopVisibility();
        }

        Content = _buildLayout();
        SystemBackdrop = new MicaBackdrop();
        _speak.Click += (_, _) => _speakAction?.Invoke();
        AppWindow.Closing += (_, args) =>
        {
            if (!_closing)
            {
                args.Cancel = true;
                _dismissFromUi();
            }
        };
    }

    internal event Action<bool>? Dismissed;
    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    internal bool IsVisible => AppWindow.IsVisible;
    internal Guid DesktopId { get; }
    internal nint SourceWindow { get; set; }

    // 更新本次状态与操作，按内容自适应窗口宽高并保留原应用焦点。
    internal void _display(Point anchor, string heading, string body, Action? speak = null,
        Rectangle? selectionBounds = null)
    {
        _lastAnchor = anchor;
        _lastSelectionBounds = selectionBounds;
        _scroll.ChangeView(null, 0, null, disableAnimation: true);
        _speakAction = speak;
        _speak.Visibility = speak is not null ? Visibility.Visible : Visibility.Collapsed;
        _notice.Visibility = Visibility.Collapsed;

        if (speak is not null)
        {
            _applyWordState(heading, body);
        }
        else
        {
            _applyGeneralState(heading, body);
        }

        _actions.Visibility = Visibility.Collapsed;
        _updateDragRegion(speak is not null);
        _placeWithFittedSize(anchor);
        _updateDesktopVisibility();
        if (_desktopWatcher is { IsRunning: false })
        {
            _desktopWatcher.Start();
        }
    }

    // 在窗口首次显示前排除任务栏和任务切换入口。
    private void _excludeFromTaskbar()
    {
        try
        {
            AppWindow.IsShownInSwitchers = false;
        }
        catch (NotImplementedException)
        {
            // Shell 接口不可用时，用 Windows 的工具窗口样式屏蔽任务栏按钮。
            nint handle = Handle;
            int style = _getWindowStyle(handle, ExtendedStyle);
            _setWindowStyle(handle, ExtendedStyle, (style | ToolWindowStyle) & ~AppWindowStyle);
        }
    }

    // 排除任务切换的窗口不再由 Shell 随虚拟桌面切换，显示状态由来源桌面决定。
    private void _updateDesktopVisibility()
    {
        bool onLookupDesktop = DesktopId == Guid.Empty || VirtualDesktop._isCurrent(DesktopId, SourceWindow);
        if (onLookupDesktop && !IsVisible)
        {
            AppWindow.Show(activateWindow: false);
            _outsideClicks._start();
        }
        else if (!onLookupDesktop && IsVisible)
        {
            _hidePopup();
        }
    }

    // 只禁用译文浮窗的系统显隐过渡，切换桌面时隐藏操作即时生效。
    private void _disableWindowTransitions()
    {
        int disabled = 1;
        int result = _setDwmWindowAttribute(Handle, DisableDwmTransitions, ref disabled, sizeof(int));
        if (result < 0)
        {
            Trace.TraceWarning("译文窗口无法禁用系统过渡（{0:X8}）。", result);
        }
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int _setDwmWindowAttribute(nint window, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int _getWindowStyle(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int _setWindowStyle(nint window, int index, int style);

    // 隐藏浮窗并释放本次结果的按钮回调。
    internal void _hide()
    {
        _desktopWatcher?.Stop();
        _loadingRing.IsActive = false;
        _hidePopup();
        _speakAction = null;
    }

    // 暂时隐藏或主动收起时都结束点外监听。
    private void _hidePopup()
    {
        _outsideClicks._stop();
        if (IsVisible)
        {
            AppWindow.Hide();
        }
    }

    // 在操作区旁显示语音错误，并按需重新适配窗口尺寸以保证提示完整可见。
    internal void _showNotice(string message)
    {
        _notice.Text = message;
        _notice.Visibility = Visibility.Visible;
        _actions.Visibility = Visibility.Visible;
        if (IsVisible)
        {
            _placeWithFittedSize(_lastAnchor);
        }
    }

    // 退出应用时关闭窗口，避免进入复用浮窗的隐藏逻辑。
    internal void _close()
    {
        _desktopWatcher?.Stop();
        _outsideClicks._stop();
        _closing = true;
        Close();
    }

    // 配置单词结果的层次：主词条、紧邻音标与同排朗读按钮、下方词义正文。
    private void _applyWordState(string word, string body)
    {
        (string meaning, string? phonetic) = _splitWordBody(body);
        _wordHeader.Visibility = Visibility.Visible;
        _statusHeader.Visibility = Visibility.Collapsed;
        _loadingRing.IsActive = false;
        _heading.Text = word;
        if (phonetic is not null)
        {
            _phonetic.Text = phonetic;
            _phoneticBadge.Visibility = Visibility.Visible;
        }
        else
        {
            _phonetic.Text = "";
            _phoneticBadge.Visibility = Visibility.Collapsed;
        }

        _bodyCard.ClearValue(FrameworkElement.StyleProperty);
        _body.Margin = new Thickness(0, 0, 2, 0);
        _applyOptionalStyle(_body, "LookupMeaningStyle");
        _body.Text = meaning;
    }

    // 配置短语译文、加载中和异常提示的视觉状态。
    private void _applyGeneralState(string heading, string body)
    {
        _wordHeader.Visibility = Visibility.Collapsed;
        _statusHeader.Visibility = Visibility.Visible;
        _statusTitle.Text = heading;

        if (heading == "正在查词")
        {
            _loadingRing.IsActive = true;
            _loadingRing.Visibility = Visibility.Visible;
            _statusIcon.Visibility = Visibility.Collapsed;
            _statusDivider.Visibility = Visibility.Collapsed;
            _bodyCard.ClearValue(FrameworkElement.StyleProperty);
            _body.Margin = new Thickness(0, 0, 2, 0);
            _applyOptionalStyle(_body, "LookupBodySecondaryStyle");
        }
        else if (heading == "译文")
        {
            _loadingRing.IsActive = false;
            _loadingRing.Visibility = Visibility.Collapsed;
            _statusIcon.Visibility = Visibility.Collapsed;
            _statusDivider.Visibility = Visibility.Visible;
            _bodyCard.ClearValue(FrameworkElement.StyleProperty);
            _body.Margin = new Thickness(0, 0, 2, 0);
            _applyOptionalStyle(_body, "LookupPhraseMeaningStyle");
        }
        else
        {
            _loadingRing.IsActive = false;
            _loadingRing.Visibility = Visibility.Collapsed;
            _statusIcon.Glyph = heading == "查词失败"
                ? "\uEA39"
                : heading == "需要 DeepSeek API Key" ? "\uE8D7" : "\uE946";
            _applyOptionalStyle(_statusIcon, heading == "查词失败"
                ? "LookupStatusIconErrorStyle"
                : heading == "需要 DeepSeek API Key" ? "LookupStatusIconCautionStyle" : "LookupStatusIconInfoStyle");
            _statusIcon.Visibility = Visibility.Visible;
            _statusDivider.Visibility = Visibility.Collapsed;
            _bodyCard.ClearValue(FrameworkElement.StyleProperty);
            _body.Margin = new Thickness(0, 0, 2, 0);
            _applyOptionalStyle(_body, "LookupStatusBodyStyle");
        }

        _body.Text = body;
    }

    // 临时按末尾 /.../ 行拆分词义和美式音标，便于独立排版；后续可改由 Codex 显式传参。
    private static (string Meaning, string? Phonetic) _splitWordBody(string body)
    {
        int lastBreak = body.LastIndexOf('\n');
        if (lastBreak > 0)
        {
            string tail = body[(lastBreak + 1)..].Trim();
            if (tail.Length >= 3 && tail[0] == '/' && tail[^1] == '/')
            {
                string head = body[..lastBreak].TrimEnd('\r', '\n');
                if (!string.IsNullOrWhiteSpace(head))
                {
                    return (head, tail);
                }
            }
        }
        return (body, null);
    }

    // 按内容测算紧凑卡片宽度，短文本紧凑收拢，长文本放宽至上限以提升可读性。
    private int _measureTargetWidth()
    {
        double maxInner = MaxCardWidth - HorizontalPadding;
        _headerHost.Measure(new Windows.Foundation.Size(maxInner, double.PositiveInfinity));
        double headerWidth = _headerHost.DesiredSize.Width;

        double bodyCardPadding = _bodyCard.BorderThickness.Left + _bodyCard.BorderThickness.Right
            + _body.Margin.Left + _body.Margin.Right;
        _body.Measure(new Windows.Foundation.Size(Math.Max(80, maxInner - bodyCardPadding), double.PositiveInfinity));
        double bodyWidth = _body.DesiredSize.Width + bodyCardPadding;

        double actionsWidth = 0;
        if (_actions.Visibility == Visibility.Visible)
        {
            _actions.Measure(new Windows.Foundation.Size(maxInner, double.PositiveInfinity));
            actionsWidth = _actions.DesiredSize.Width;
        }

        double needed = Math.Max(headerWidth, Math.Max(bodyWidth, actionsWidth)) + HorizontalPadding + 4;
        return Math.Clamp((int)Math.Ceiling(needed), MinCardWidth, MaxCardWidth);
    }

    // 测量当前内容所需的逻辑宽高并放到选区附近。
    private void _placeWithFittedSize(Point anchor)
    {
        int width = _measureTargetWidth();
        _lastWidth = width;
        _root.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        int measured = (int)Math.Ceiling(_root.DesiredSize.Height) + 2;
        int desiredHeight = Math.Clamp(measured, MinCardHeight, MaxCardHeight);
        _lastHeight = desiredHeight;

        int requested = Math.Max(48, desiredHeight - _clientHeightOffset);
        WindowPlacement._place(this, anchor, width, requested, centered: false, _lastSelectionBounds);
        if (AppWindow.ClientSize.Width > 0 && AppWindow.ClientSize.Height > 0)
        {
            double scale = _root.XamlRoot?.RasterizationScale ?? (double)AppWindow.ClientSize.Width / width;
            int extra = (int)Math.Round(AppWindow.ClientSize.Height / scale - desiredHeight);
            if (Math.Abs(extra) >= 2)
            {
                // 某些系统缩放下实际客户区比请求值高，校正后保持卡片紧凑。
                _clientHeightOffset = Math.Clamp(_clientHeightOffset + extra, 0, 64);
                int corrected = Math.Max(48, desiredHeight - _clientHeightOffset);
                if (corrected != requested)
                {
                    WindowPlacement._place(this, anchor, width, corrected, centered: false, _lastSelectionBounds);
                }
            }
        }
    }

    // 首次渲染控件模板后，在尺寸偏差时微调一次。
    private void _fitAfterFirstLoad()
    {
        if (_hasLoadedOnce)
        {
            return;
        }
        _hasLoadedOnce = true;
        _root.XamlRoot.Changed += (_, _) => _syncDragRegion();
        _syncDragRegion();
        if (!IsVisible)
        {
            return;
        }
        _root.Measure(new Windows.Foundation.Size(_lastWidth, double.PositiveInfinity));
        int fitted = Math.Clamp((int)Math.Ceiling(_root.DesiredSize.Height) + 2, MinCardHeight, MaxCardHeight);
        double clientHeight = AppWindow.ClientSize.Height / _root.XamlRoot.RasterizationScale;
        if (Math.Abs(fitted - _lastHeight) >= 3 || Math.Abs(clientHeight - fitted) >= 3)
        {
            _placeWithFittedSize(_lastAnchor);
        }
    }

    // 点外或按 Esc 收起浮窗，保留回到来源窗口完成手动复制的机会。
    private void _dismissFromUi(bool keepManualCopy = false)
    {
        if (!IsVisible)
        {
            return;
        }
        _hide();
        Dismissed?.Invoke(keepManualCopy);
    }
}
