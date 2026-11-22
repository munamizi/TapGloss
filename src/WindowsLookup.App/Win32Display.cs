using System.Drawing;
using System.Runtime.InteropServices;

namespace WindowsLookup.App;

/// <summary>读取原生屏幕坐标和显示器工作区。</summary>
internal static class Win32Display
{
    private const uint DefaultToNearest = 2;

    // 取得物理像素鼠标位置，接口失败时返回原点。
    internal static Point _getCursorPosition() => _getCursor(out Point point) ? point : Point.Empty;

    // 返回包含坐标的显示器，坐标在屏幕外时取最近者。
    internal static nint _monitorForPoint(Point point) => _monitorFromPoint(point, DefaultToNearest);

    // 返回窗口所在或最近的显示器。
    internal static nint _monitorForWindow(nint window) => _monitorFromWindow(window, DefaultToNearest);

    // 读取目标显示器工作区，显示器在读取间移除时使用主屏工作区。
    internal static Rectangle _getWorkingArea(nint monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && _getMonitorInfo(monitor, ref info))
        {
            return info.Work.ToRectangle;
        }
        return _getPrimaryWorkArea(0x30, 0, out var work, 0)
            ? work.ToRectangle
            : new Rectangle(0, 0, Math.Max(1, _getMetric(0)), Math.Max(1, _getMetric(1)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rectangle ToRectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal Bounds Monitor, Work;
        internal uint Flags;
    }

    // 查询鼠标位置。
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _getCursor(out Point point);

    // 按坐标取得显示器句柄。
    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern nint _monitorFromPoint(Point point, uint flags);

    // 按窗口取得显示器句柄。
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern nint _monitorFromWindow(nint window, uint flags);

    // 取得显示器边界和工作区。
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _getMonitorInfo(nint monitor, ref MonitorInfo info);

    // 读取主屏工作区。
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _getPrimaryWorkArea(uint action, uint parameter, out Bounds work, uint flags);

    // 读取主屏宽度或高度。
    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int _getMetric(int index);
}
