using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WindowsLookup.App;

/// <summary>用 Windows 通知区域图标和 WinUI 菜单提供常驻入口。</summary>
internal sealed class WindowsTray : IDisposable
{
    private const uint TrayCallback = 0x8001;
    private const uint AddIcon = 0, ModifyIcon = 1, DeleteIcon = 2;
    private readonly Win32MessageWindow _window;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private readonly string _tip;
    private readonly Action _configureKey;
    private readonly Func<TrayMenuWindow> _createMenu;
    private TrayMenuWindow? _menu;
    private TrayMenuWindow? _menuAtRightDown;
    private (Point Point, long Time)? _outsideRightRelease;
    private bool _disposed;

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
        _configureKey = configureKey;
        _tip = _truncate(text, 127);
        _createMenu = () => new TrayMenuWindow(configureKey, currentMode(), selectMode, exit,
            point => _outsideRightRelease = (point, Environment.TickCount64));
        _taskbarCreated = _registerWindowMessage("TaskbarCreated");
        if (_taskbarCreated == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using Stream iconStream = typeof(WindowsTray).Assembly.GetManifestResourceStream("WindowsLookup.App.TrayIcon.ico")
            ?? throw new InvalidOperationException("缺少内置的托盘图标。");
        using var bytes = new MemoryStream();
        iconStream.CopyTo(bytes);
        _icon = Win32TrayIcon._load(bytes.ToArray());
        try
        {
            // TaskbarCreated 是系统广播，托盘必须使用顶层隐藏窗口。
            _window = new Win32MessageWindow(messageOnly: false, _onMessage);
        }
        catch
        {
            Win32TrayIcon._destroy(_icon);
            throw;
        }
        _addIcon();
    }

    internal bool IsMenuOpen => _menu is not null;

    // 使用旧版通知图标的回调格式，延续右键去重和双击设置交互。
    private bool _onMessage(uint message, nint wParam, nint lParam)
    {
        if (message == _taskbarCreated)
        {
            _addIcon();
            return true;
        }
        if (message != TrayCallback || wParam != 1)
        {
            return false;
        }
        switch ((int)lParam)
        {
            case 0x0204:
                _menuAtRightDown = _menu;
                break;
            case 0x0205:
                _toggleMenu(Win32Display._getCursorPosition());
                break;
            case 0x0202:
                _menu?._dismiss();
                break;
            case 0x0203:
                _menu?._dismiss();
                _configureKey();
                break;
        }
        return true;
    }

    // Explorer 重建任务栏后用相同标识恢复提示、图标和回调。
    private void _addIcon()
    {
        var data = _iconData();
        data.Flags = 0x01 | 0x02 | 0x04;
        if (!_shellNotify(AddIcon, ref data))
        {
            Trace.TraceWarning("TapGloss 通知区域图标尚未注册。");
        }
    }

    // 使用 Windows 系统通知展示短状态。
    internal void _notify(string title, string message, bool warning = false)
    {
        var data = _iconData();
        data.Flags = 0x10;
        data.InfoTitle = _truncate(title, 63);
        data.Info = _truncate(message, 255);
        data.Timeout = 3000;
        data.InfoFlags = warning ? 0x02u : 0x01u;
        _shellNotify(ModifyIcon, ref data);
    }

    // 复用本窗口与图标编号标识，未选择的缓冲字段保持为空。
    private NotifyData _iconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyData>(),
        Window = _window.Handle,
        Id = 1,
        Callback = TrayCallback,
        Icon = _icon,
        Tip = _tip,
        Info = "",
        InfoTitle = "",
    };

    // 固定长 UTF-16 字段按代码单元截断，避免切断代理对。
    private static string _truncate(string text, int length) =>
        text.Length <= length ? text : text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];

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
    /// 关闭菜单，移除通知图标并释放窗口和图标句柄。
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
        try
        {
            _menu?._dismiss();
        }
        finally
        {
            var data = _iconData();
            _shellNotify(DeleteIcon, ref data);
            Win32TrayIcon._destroy(_icon);
            _window.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Callback;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }

    // 注册 Explorer 重建任务栏时广播的消息。
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint _registerWindowMessage(string name);

    // 增删通知区域图标或提交系统通知。
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _shellNotify(uint operation, ref NotifyData data);
}
