using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace WindowsLookup.App;

/// <summary>在浮窗显示期间监听点外操作，并让原始鼠标事件继续传递。</summary>
internal sealed class OutsideClickWatcher
{
    private const int LowLevelMouse = 14;
    private const int LeftButtonDown = 0x0201;
    private const int RightButtonDown = 0x0204;
    private const int RightButtonUp = 0x0205;
    private const int MiddleButtonDown = 0x0207;
    private const int ExtraButtonDown = 0x020B;
    private const uint Root = 2;
    private const uint Owner = 4;

    private readonly DispatcherQueue _dispatcher;
    private readonly nint _window;
    private readonly Action<nint> _outside;
    private readonly int _rightButtonMessage;
    private readonly MouseHook _callback;
    private nint _hook;
    private int _generation;

    /// <summary>
    /// 创建跟随浮窗显示周期的点外监听。
    ///
    /// Args:
    ///     dispatcher - 用于在界面线程处理点外操作的队列。
    ///     window - 浮窗及其子弹层归属的窗口句柄。
    ///     outside - 收到点外操作时的回调，参数是点击目标的顶层窗口。
    ///     rightButtonOnRelease - 是否在右键松开时判断点外。
    /// Returns:
    ///     OutsideClickWatcher - 可随窗口复用的监听器。
    /// </summary>
    internal OutsideClickWatcher(DispatcherQueue dispatcher, nint window, Action<nint> outside,
        bool rightButtonOnRelease = false)
    {
        _dispatcher = dispatcher;
        _window = window;
        _outside = outside;
        _rightButtonMessage = rightButtonOnRelease ? RightButtonUp : RightButtonDown;
        _callback = _onMouse;
    }

    // 浮窗显示时注册鼠标监听，内容刷新时复用当前注册。
    internal void _start()
    {
        if (_hook != 0)
        {
            return;
        }
        _hook = _setMouseHook(LowLevelMouse, _callback, _currentModule(0), 0);
        if (_hook == 0)
        {
            Trace.TraceWarning("点外监听注册失败（{0}）。", Marshal.GetLastPInvokeError());
        }
    }

    // 隐藏时注销监听，同时废弃旧一轮显示已排队的点外操作。
    internal void _stop()
    {
        if (_hook != 0)
        {
            _generation++;
            _removeMouseHook(_hook);
            _hook = 0;
        }
    }

    // 只处理按键，与 Win32 使用同一组屏幕物理坐标。
    private nint _onMouse(int code, nint message, nint data)
    {
        int button = (int)message;
        if (code == 0 && (button is LeftButtonDown or MiddleButtonDown or ExtraButtonDown || button == _rightButtonMessage))
        {
            var point = new Point(Marshal.ReadInt32(data), Marshal.ReadInt32(data, sizeof(int)));
            int generation = _generation;
            _dispatcher.TryEnqueue(() => _dispatchClick(point, generation));
        }
        return _callNextMouseHook(0, code, message, data);
    }

    // 在消息队列上判断真实命中窗口，允许本窗拥有的子弹层继续交互。
    private void _dispatchClick(Point point, int generation)
    {
        if (_hook == 0 || generation != _generation)
        {
            return;
        }
        nint target = _getAncestor(_windowFromPoint(point), Root);
        nint owner = target;
        while (owner != 0 && owner != _window)
        {
            owner = _getWindow(owner, Owner);
        }
        if (owner == 0)
        {
            _outside(target);
        }
    }

    private delegate nint MouseHook(int code, nint message, nint data);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static extern nint _currentModule(nint name);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint _setMouseHook(int type, MouseHook callback, nint module, uint thread);

    [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static extern nint _callNextMouseHook(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx")]
    private static extern bool _removeMouseHook(nint hook);

    [DllImport("user32.dll", EntryPoint = "WindowFromPhysicalPoint")]
    private static extern nint _windowFromPoint(Point point);

    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern nint _getAncestor(nint window, uint kind);

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern nint _getWindow(nint window, uint relation);
}
