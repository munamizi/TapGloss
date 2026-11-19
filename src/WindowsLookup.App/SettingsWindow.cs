using Point = System.Drawing.Point;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace WindowsLookup.App;

/// <summary>以与查词浮窗一致的 WinUI 卡片风格展示设置字段、保存操作与错误提示。</summary>
internal sealed class SettingsWindow : Window
{
    private const int DialogWidth = 372;
    private const int MaxDialogHeight = 420;

    private readonly Grid _root = new();
    private readonly Grid _titleDragArea = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ColumnDefinition _titleSpacer = new() { Width = new GridLength(48) };
    private readonly Button _save = new() { Content = "保存", MinWidth = 76 };
    private readonly StackPanel _noticeBanner = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _notice = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Point _lastAnchor;
    private int _minHeight;
    private int _titleBarHeightOverhead;
    private uint _titleBarDpi;

    /// <summary>
    /// 创建可保存或关闭的设置窗口。
    ///
    /// Args:
    ///     title - 窗口标题
    ///     fields - 设置所需的输入控件
    ///     save - 保存回调，成功返回 null，失败返回要显示的提示
    /// Returns:
    ///     SettingsWindow - 等待显示的设置窗口。
    /// </summary>
    internal SettingsWindow(string title, FrameworkElement fields, Func<string?> save)
    {
        Title = title;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        _applyOptionalStyle(_save, "LookupPrimaryButtonStyle");
        _applyOptionalStyle(_notice, "LookupNoticeTextStyle");

        var cancel = new Button { Content = "取消", MinWidth = 76 };
        cancel.Click += (_, _) => Close();
        _save.Click += (_, _) =>
        {
            string? error = save();
            if (error is null)
            {
                Close();
            }
            else
            {
                _showError(error);
            }
        };

        // 顶部延展标题区保留清晰标题与关闭入口，淡化窗口外框割裂感。
        var titleRow = new Grid { Height = 36 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(_titleSpacer);

        var titleText = new TextBlock
        {
            Text = title,
            Margin = new Thickness(20, 2, 8, 0),
        };
        _applyOptionalStyle(titleText, "LookupDialogTitleStyle");
        _titleDragArea.Children.Add(titleText);
        titleRow.Children.Add(_titleDragArea);

        var noticeIcon = new FontIcon
        {
            Glyph = "\uEA39",
            Margin = new Thickness(0, 2, 0, 0),
        };
        _applyOptionalStyle(noticeIcon, "LookupStatusIconErrorStyle");
        noticeIcon.VerticalAlignment = VerticalAlignment.Top;
        _noticeBanner.Children.Add(noticeIcon);
        _noticeBanner.Children.Add(_notice);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 2, 0, 0),
        };
        actions.Children.Add(_save);
        actions.Children.Add(cancel);

        var bodyLayout = new StackPanel { Padding = new Thickness(20, 6, 20, 18), Spacing = 12 };
        bodyLayout.Children.Add(fields);
        bodyLayout.Children.Add(_noticeBanner);
        bodyLayout.Children.Add(actions);

        var scroll = new ScrollViewer
        {
            Content = bodyLayout,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(titleRow);
        _root.Children.Add(scroll);
        Grid.SetRow(scroll, 1);

        _root.KeyDown += (_, key) =>
        {
            if (key.Key == VirtualKey.Escape)
            {
                key.Handled = true;
                Close();
            }
        };
        _root.ActualThemeChanged += (_, _) => _updateTitleBarTheme();
        _root.Loaded += (_, _) =>
        {
            _updateTitleBarTheme();
            _updateTitleRightInset();
            _fitHeight();
        };

        Content = _root;
        _configureTitleBar();
        SystemBackdrop = new MicaBackdrop();
        Closed += (_, _) => _closed.TrySetResult();
    }

    // 随设置字段的有效性更新保存按钮。
    internal void _setSaveEnabled(bool enabled) => _save.IsEnabled = enabled;

    // 在目标屏幕居中显示，等待保存或关闭。
    internal Task _showAsync(Point anchor, int height)
    {
        _lastAnchor = anchor;
        _minHeight = height;
        _fitHeight();
        Activate();
        return _closed.Task;
    }

    // 显示保存失败原因，并按需扩高窗口保证错误提示与操作按钮完整可见。
    private void _showError(string error)
    {
        _notice.Text = error;
        _noticeBanner.Visibility = Visibility.Visible;
        _fitHeight();
    }

    // 按当前内容和传入基线高度调整设置窗口尺寸，同时抵消扩展标题栏附带的高度差。
    private void _fitHeight()
    {
        _root.Measure(new Windows.Foundation.Size(DialogWidth, double.PositiveInfinity));
        int desired = (int)Math.Ceiling(_root.DesiredSize.Height) + 2;
        int targetHeight = Math.Clamp(Math.Max(_minHeight, desired), _minHeight, MaxDialogHeight);
        WindowPlacement._fit(this, _lastAnchor, DialogWidth, targetHeight, minimumHeight: 120, centered: true,
            ref _titleBarHeightOverhead, ref _titleBarDpi);
    }

    // 配置扩展标题栏，隐藏默认应用图标并保持关闭按钮透明融合。
    private void _configureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_titleDragArea);
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }
        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _updateTitleBarTheme();
        _updateTitleRightInset();
    }

    // 同步浅色、深色主题下标题栏关闭按钮的可见对比度。
    private void _updateTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }
        bool isDark = _root.ActualTheme == ElementTheme.Dark;
        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = isDark
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE4, 0x00, 0x00, 0x00);
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x9E, 0x00, 0x00, 0x00);
    }

    // 按当前 DPI 同步标题栏右侧关闭按钮的预留宽度。
    private void _updateTitleRightInset()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }
        int rightInset = AppWindow.TitleBar.RightInset;
        if (rightInset > 0)
        {
            double scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
            _titleSpacer.Width = new GridLength(Math.Max(48, Math.Ceiling(rightInset / scale)));
        }
    }

    // 按资源键安全读取共享样式。
    private static void _applyOptionalStyle(FrameworkElement element, string resourceKey)
    {
        if (_resource<Style>(resourceKey) is { } style)
        {
            element.Style = style;
        }
    }

    // 从应用资源字典读取指定类型的共享资源。
    private static T? _resource<T>(string key) where T : class =>
        Application.Current?.Resources.TryGetValue(key, out object? value) == true ? value as T : null;
}
