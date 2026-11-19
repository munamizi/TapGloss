using System.Drawing;
using Forms = System.Windows.Forms;

namespace WindowsLookup.App;

/// <summary>用 Windows 通知区域图标和 WinUI 菜单提供常驻入口。</summary>
internal sealed class WindowsTray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _trayIcon;
    private readonly Func<TrayMenuWindow> _createMenu;
    private TrayMenuWindow? _menu;
    private TrayMenuWindow? _menuAtRightDown;
    private (Point Point, long Time)? _outsideRightRelease;

    /// <summary>
    /// 创建托盘图标并绑定密钥设置、模式切换和退出。
    ///
    /// Args:
    ///     text - 托盘图标提示
    ///     configureKey - 打开密钥设置的回调
    ///     currentMode - 读取当前 Amy 模式的回调
    ///     selectMode - 保存并应用所选 Amy 模式的回调
    ///     exit - 退出查词程序的回调
    /// Returns:
    ///     WindowsTray - 尚可更新状态的托盘组件。
    /// </summary>
    internal WindowsTray(string text, Action configureKey, Func<SpeechMemoryMode> currentMode,
        Action<SpeechMemoryMode> selectMode, Action exit)
    {
        _createMenu = () => new TrayMenuWindow(configureKey, currentMode(), selectMode, exit,
            point => _outsideRightRelease = (point, Environment.TickCount64));
        using Stream iconStream = typeof(WindowsTray).Assembly.GetManifestResourceStream("WindowsLookup.App.TrayIcon.ico")
            ?? throw new InvalidOperationException("缺少内置的托盘图标。");
        _trayIcon = new Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        _icon = new Forms.NotifyIcon { Icon = _trayIcon, Text = text, Visible = true };
        _icon.MouseDown += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Right)
            {
                _menuAtRightDown = _menu;
            }
        };
        _icon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Right)
            {
                _toggleMenu(Forms.Cursor.Position);
            }
            else
            {
                _menu?._dismiss();
            }
        };
        _icon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                _menu?._dismiss();
                configureKey();
            }
        };
    }

    internal bool IsMenuOpen => _menu is not null;

    // 使用 Windows 系统通知展示短状态。
    internal void _notify(string title, string message, bool warning = false) =>
        _icon.ShowBalloonTip(3000, title, message, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    // 同一次右键只能收起按下时的菜单，忽略点外监听先收到的那次松开。
    private void _toggleMenu(Point point)
    {
        TrayMenuWindow? previous = _menuAtRightDown ?? _menu;
        _menuAtRightDown = null;
        var outsideRelease = _outsideRightRelease;
        _outsideRightRelease = null;
        if (previous is not null)
        {
            previous._dismiss();
            return;
        }
        if (outsideRelease is { } release && Environment.TickCount64 - release.Time < 150
            && Math.Abs((long)release.Point.X - point.X) <= 4 && Math.Abs((long)release.Point.Y - point.Y) <= 4)
        {
            return;
        }

        var menu = _createMenu();
        _menu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu))
            {
                _menu = null;
            }
        };
        menu._open(point);
    }

    /// <summary>
    /// 关闭菜单，移除通知区域图标并释放图标资源。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值。
    /// </summary>
    public void Dispose()
    {
        _menu?._dismiss();
        _icon.Dispose();
        _trayIcon.Dispose();
    }
}
