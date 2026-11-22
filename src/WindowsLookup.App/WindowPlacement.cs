using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace WindowsLookup.App;

/// <summary>按所在屏幕和 DPI 设置 WinUI 窗口的位置与尺寸。</summary>
internal static class WindowPlacement
{
    // 先让跨屏窗口在目标位置附近取得新 DPI，再校正客户区的物理高度。
    internal static void _fit(Window window, Point anchor, int width, int height, int minimumHeight, bool centered,
        ref int heightOffset, ref uint lastDpi, Rectangle? selectionBounds = null)
    {
        Point monitorPoint = selectionBounds is { } bounds ? new Point(bounds.Right - 1, bounds.Bottom - 1) : anchor;
        nint target = Win32Display._monitorForPoint(monitorPoint);
        Rectangle screen = Win32Display._getWorkingArea(target);
        AppWindow appWindow = window.AppWindow;
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (target != Win32Display._monitorForWindow(handle))
        {
            appWindow.Move(_position(screen, anchor, appWindow.Size, _getDpi(handle) / 96.0, centered, selectionBounds));
        }

        uint dpi = _getDpi(handle);
        if (lastDpi != dpi)
        {
            lastDpi = dpi;
            heightOffset = 0;
        }
        double scale = dpi / 96.0;
        int requested = Math.Max(minimumHeight, height - heightOffset);
        int physicalHeight = (int)Math.Round(requested * scale);
        int physicalWidth = Math.Min(screen.Width, (int)Math.Round(width * scale));
        appWindow.ResizeClient(new SizeInt32(physicalWidth, Math.Min(screen.Height, physicalHeight)));
        if (physicalHeight < screen.Height && appWindow.ClientSize.Height > 0)
        {
            int extra = (int)Math.Round(appWindow.ClientSize.Height / scale - height);
            if (Math.Abs(extra) >= 2)
            {
                heightOffset = Math.Clamp(heightOffset + extra, 0, 64);
                int corrected = Math.Max(minimumHeight, height - heightOffset);
                if (corrected != requested)
                {
                    appWindow.ResizeClient(new SizeInt32(physicalWidth,
                        Math.Min(screen.Height, (int)Math.Round(corrected * scale))));
                }
            }
        }

        appWindow.Move(_position(screen, anchor, appWindow.Size, scale, centered, selectionBounds));
    }

    // 计算选区或鼠标附近的位置，并把浮窗限制在目标屏幕工作区内。
    private static PointInt32 _position(Rectangle screen, Point anchor, SizeInt32 size, double scale, bool centered,
        Rectangle? selectionBounds)
    {
        int x = centered ? screen.Left + (screen.Width - size.Width) / 2 : anchor.X + 12;
        int y = centered ? screen.Top + (screen.Height - size.Height) / 2 : anchor.Y + 18;
        if (selectionBounds is { } selection)
        {
            int gap = (int)Math.Round(4 * scale);
            x = selection.Right;
            y = selection.Bottom + gap;
            if (y + size.Height > screen.Bottom && selection.Top - gap - size.Height >= screen.Top)
            {
                y = selection.Top - gap - size.Height;
            }
        }
        return new PointInt32(
            Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - size.Width)),
            Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height)));
    }

    // 使用窗口正在使用的原生 DPI，供物理像素与逻辑尺寸之间换算。
    internal static uint _getDpi(nint window) => Math.Max(_getDpiForWindow(window), 96u);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint _getDpiForWindow(nint window);
}
