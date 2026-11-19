using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Forms = System.Windows.Forms;

namespace WindowsLookup.App;

/// <summary>为托盘上的 WinUI 原生菜单提供一个临时锚点。</summary>
internal sealed class TrayMenuWindow : Window
{
    private const int AnchorSize = 2;
    private const int ExtendedStyle = -20;
    private const int LayeredWindow = 0x00080000;
    private const int NoActivateWindow = 0x08000000;
    private const uint AlphaAttribute = 0x00000002;
    private readonly Grid _anchor = new();
    private readonly MenuFlyout _flyout = new()
    {
        ShouldConstrainToRootBounds = false,
        AreOpenCloseAnimationsEnabled = false,
    };
    private readonly OutsideClickWatcher _outsideClicks;
    private WindowsHotkey? _escape;
    private bool _closed;
    private bool _dismissing;
    private Action? _selectedAction;

    /// <summary>
    /// 为 WinUI 菜单配置不抢占焦点的透明锚点，菜单收起后执行选中的命令。
    ///
    /// Args:
    ///     configureKey - 打开密钥设置的回调。
    ///     currentMode - 打开菜单时选中的 Amy 模式。
    ///     selectMode - 用户选择 Amy 模式后执行的回调。
    ///     exit - 退出程序的回调。
    /// Returns:
    ///     TrayMenuWindow - 可从托盘位置打开一次的菜单宿主。
    /// </summary>
    internal TrayMenuWindow(Action configureKey, SpeechMemoryMode currentMode,
        Action<SpeechMemoryMode> selectMode, Action exit)
    {
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.PreferredMinimumWidth = 1;
        presenter.PreferredMinimumHeight = 1;
        try
        {
            AppWindow.IsShownInSwitchers = false;
        }
        catch (NotImplementedException)
        {
        }

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int style = _getWindowStyle(handle, ExtendedStyle);
        _setWindowStyle(handle, ExtendedStyle, style | LayeredWindow | NoActivateWindow);
        _setWindowAlpha(handle, 0, 0, AlphaAttribute);
        _outsideClicks = new OutsideClickWatcher(DispatcherQueue, handle, _ => _dismiss(), rightButtonOnRelease: true);

        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 112d));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 180d));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        _flyout.MenuFlyoutPresenterStyle = presenterStyle;
        var configure = new MenuFlyoutItem { Text = "设置 API Key" };
        _bindCommand(configure, configureKey);
        _flyout.Items.Add(configure);

        var modes = new MenuFlyoutSubItem
        {
            Text = "模式切换",
            FontSize = 12,
            MinHeight = 0,
            Height = 27,
            Padding = new Thickness(10, 0, 10, 0),
        };
        foreach (var (mode, label) in new[]
        {
            (SpeechMemoryMode.DynamicWarmth, "动态续热"),
            (SpeechMemoryMode.Resident, "静态常驻"),
        })
        {
            var option = new RadioMenuFlyoutItem
            {
                Text = label,
                GroupName = "AmyMemoryMode",
                IsChecked = currentMode == mode,
            };
            _bindCommand(option, () => selectMode(mode));
            option.Padding = new Thickness(6, 0, 6, 0);
            if (Application.Current.Resources.TryGetValue("TapGlossModeMenuItemStyle", out object? resource)
                && resource is Style modeItemStyle)
            {
                option.Style = modeItemStyle;
            }
            option.Loaded += _fitModeSubmenu;
            modes.Items.Add(option);
        }
        _flyout.Items.Add(modes);

        var quit = new MenuFlyoutItem { Text = "退出" };
        _bindCommand(quit, exit);
        _flyout.Items.Add(quit);
        _flyout.Closed += (_, _) =>
        {
            _stopListeningForDismissal();
            DispatcherQueue.TryEnqueue(() =>
            {
                Action? select = _selectedAction;
                _selectedAction = null;
                _closeAnchor();
                select?.Invoke();
            });
        };
        _anchor.Loaded += _showFlyout;
        Closed += (_, _) =>
        {
            _closed = true;
            _stopListeningForDismissal();
        };
        Content = _anchor;
    }

    // 应用紧凑菜单样式，动作留到菜单关闭后执行。
    private void _bindCommand(MenuFlyoutItem item, Action select)
    {
        item.FontSize = 12;
        item.MinHeight = 0;
        item.Height = 27;
        item.Padding = new Thickness(10, 0, 10, 0);
        item.Click += (_, _) => _selectedAction = select;
    }

    // 仅让模式子菜单按两行内容收紧，保留主菜单的最小宽度。
    private static void _fitModeSubmenu(object sender, RoutedEventArgs args)
    {
        DependencyObject? element = (DependencyObject)sender;
        while (element is not null)
        {
            if (element is MenuFlyoutPresenter presenter)
            {
                presenter.MinWidth = 0;
                return;
            }
            element = VisualTreeHelper.GetParent(element);
        }
    }

    // 在鼠标所在屏幕上显示透明锚点，保留 Windows 托盘的前台焦点。
    internal void _open(Point point)
    {
        Rectangle workArea = Forms.Screen.FromPoint(point).WorkingArea;
        _flyout.Placement = point.Y >= workArea.Top + workArea.Height / 2
            ? FlyoutPlacementMode.Top
            : FlyoutPlacementMode.Bottom;
        AppWindow.MoveAndResize(new RectInt32(
            Math.Clamp(point.X, workArea.Left, workArea.Right - AnchorSize),
            Math.Clamp(point.Y, workArea.Top, workArea.Bottom - AnchorSize),
            AnchorSize, AnchorSize));
        AppWindow.Show(activateWindow: false);
    }

    // 以不获取焦点的方式打开菜单，单独接收点外和 Escape 关闭操作。
    private void _showFlyout(object sender, RoutedEventArgs args)
    {
        _anchor.Loaded -= _showFlyout;
        if (!_closed && !_dismissing)
        {
            _outsideClicks._start();
            try
            {
                _escape = new WindowsHotkey(0, Forms.Keys.Escape);
                _escape.Pressed += _ => _dismiss();
            }
            catch (Win32Exception)
            {
                // Escape 被其他程序占用时，仍可点外或再次右键关闭。
            }
            _flyout.ShowAt(_anchor, new FlyoutShowOptions
            {
                Placement = _flyout.Placement,
                ShowMode = FlyoutShowMode.Transient,
            });
        }
    }

    // 菜单关闭时释放临时输入监听，恢复 Escape 原来的处理方式。
    private void _stopListeningForDismissal()
    {
        _outsideClicks._stop();
        _escape?.Dispose();
        _escape = null;
    }

    // 在退出或再次点击托盘图标时收起原生菜单并关闭锚点。
    internal void _dismiss()
    {
        if (!_closed && !_dismissing)
        {
            _dismissing = true;
            if (_flyout.IsOpen)
            {
                _flyout.Hide();
            }
            else
            {
                _closeAnchor();
            }
        }
    }

    // 避免菜单的 Closed 事件与主动关闭同时销毁同一个窗口。
    private void _closeAnchor()
    {
        if (!_closed)
        {
            _closed = true;
            Close();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int _getWindowStyle(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int _setWindowStyle(nint window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetLayeredWindowAttributes")]
    private static extern bool _setWindowAlpha(nint window, uint color, byte alpha, uint flags);


}
