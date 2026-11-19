using System.Runtime.InteropServices;

namespace WindowsLookup.App;

/// <summary>通过 Windows Shell 查询窗口的虚拟桌面归属。</summary>
internal static class VirtualDesktop
{
    private const int DwmCloaked = 14;
    private static IVirtualDesktopManager? _manager;

    private const uint CloakedByShell = 2;

    // 判断窗口是否已被 Windows 切出合成画面。
    internal static bool _isShellCloaked(nint window) =>
        _getDwmWindowAttribute(window, DwmCloaked, out uint flags, sizeof(uint)) >= 0
        && (flags & CloakedByShell) != 0;

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int _getDwmWindowAttribute(nint window, int attribute, out uint value, int valueSize);

    // 优先由来源窗口判断桌面是否可见，来源关闭或移动后通过当前前台窗口核对桌面。
    internal static bool _isCurrent(Guid desktopId, nint sourceWindow)
    {
        IVirtualDesktopManager? manager = _getManager();
        if (manager is not null && sourceWindow != 0
            && manager._getWindowDesktopId(sourceWindow, out Guid sourceDesktop) >= 0
            && sourceDesktop == desktopId
            && manager._isWindowOnCurrentVirtualDesktop(sourceWindow, out bool sourceIsCurrent) >= 0)
        {
            return sourceIsCurrent && !_isShellCloaked(sourceWindow);
        }

        return _getDesktopId(_foregroundWindow()) == desktopId;
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint _foregroundWindow();

    // 使用前台应用的桌面归属识别热键是否来自另一个桌面。
    internal static Guid _getDesktopId(nint window)
    {
        if (window == 0)
        {
            return Guid.Empty;
        }
        IVirtualDesktopManager? manager = _getManager();
        if (manager is not null && manager._getWindowDesktopId(window, out Guid desktopId) >= 0)
        {
            return desktopId;
        }

        _manager = null;
        return Guid.Empty;
    }

    // 同一个界面线程复用 Shell 接口，资源管理器暂不可用时交给调用方新建窗口。
    private static IVirtualDesktopManager? _getManager()
    {
        try
        {
            return _manager ??= (IVirtualDesktopManager)(object)new VirtualDesktopManager();
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return null;
        }
    }

    [ComImport]
    [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    private sealed class VirtualDesktopManager
    {
    }

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        // 第一个 COM 方法返回指定顶层窗口是否位于当前桌面。
        [PreserveSig]
        int _isWindowOnCurrentVirtualDesktop(nint window, [MarshalAs(UnmanagedType.Bool)] out bool isCurrent);

        // 第二个 COM 方法返回指定顶层窗口所属的桌面标识。
        [PreserveSig]
        int _getWindowDesktopId(nint window, out Guid desktopId);
    }
}
