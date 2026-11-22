using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsLookup;

/// <summary>在创建线程的消息循环上接收 Win32 消息。</summary>
internal sealed class Win32MessageWindow : IDisposable
{
    private const string ClassName = "TapGloss.Win32MessageWindow";
    private const uint NonClientDestroy = 0x0082;
    private static readonly ConcurrentDictionary<nint, Win32MessageWindow> Windows = new();
    private static readonly WindowProc Procedure = _dispatch;
    private static readonly nint Module = _getModule(null);
    private static readonly Lazy<ushort> WindowClass = new(_registerClass);
    private readonly Func<uint, nint, nint, bool> _onMessage;

    internal nint Handle { get; private set; }

    /// <summary>
    /// 创建仅消息窗口或接收系统广播的隐藏顶层窗口。
    ///
    /// Args:
    ///     messageOnly - 为 true 时创建 HWND_MESSAGE 窗口。
    ///     onMessage - 消息回调，返回 true 时停止默认处理。
    /// Returns:
    ///     Win32MessageWindow - 在当前线程创建的窗口。
    /// </summary>
    internal Win32MessageWindow(bool messageOnly, Func<uint, nint, nint, bool> onMessage)
    {
        _ = WindowClass.Value;
        _onMessage = onMessage;
        Handle = _createWindow(0, ClassName, "", 0, 0, 0, 0, 0, messageOnly ? new nint(-3) : 0, 0, Module, 0);
        if (Handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        Windows[Handle] = this;
    }

    // 在进程内注册一次窗口类，并保留原生入口委托。
    private static ushort _registerClass()
    {
        var info = new WindowClassInfo
        {
            Size = (uint)Marshal.SizeOf<WindowClassInfo>(),
            Instance = Module,
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            ClassName = ClassName,
        };
        ushort atom = _register(ref info);
        return atom != 0 ? atom : throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    // 路由窗口消息，避免托管异常跨越原生回调边界。
    private static nint _dispatch(nint handle, uint message, nint wParam, nint lParam)
    {
        if (message == NonClientDestroy)
        {
            if (Windows.TryRemove(handle, out var window))
            {
                window.Handle = 0;
            }
        }
        else if (Windows.TryGetValue(handle, out var target))
        {
            try
            {
                if (target._onMessage(message, wParam, lParam))
                {
                    return 0;
                }
            }
            catch (Exception error)
            {
                Trace.TraceWarning("TapGloss 窗口消息 0x{0:X} 处理失败（{1}）。", message, error.GetType().Name);
            }
        }
        return _defaultProc(handle, message, wParam, lParam);
    }

    /// <summary>
    /// 在创建线程销毁窗口并取消回调路由。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值。
    /// </summary>
    public void Dispose()
    {
        if (Handle != 0 && !_destroy(Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint handle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassInfo
    {
        internal uint Size, Style;
        internal nint Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName, ClassName;
        internal nint SmallIcon;
    }

    // 取得当前程序的模块句柄。
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint _getModule(string? name);

    // 注册窗口类。
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", ExactSpelling = true, SetLastError = true)]
    private static extern ushort _register(ref WindowClassInfo info);

    // 创建初始不可见的窗口。
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint _createWindow(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint module, nint param);

    // 销毁当前线程拥有的窗口。
    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _destroy(nint handle);

    // 交由 Windows 处理未消费的消息。
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern nint _defaultProc(nint handle, uint message, nint wParam, nint lParam);
}
