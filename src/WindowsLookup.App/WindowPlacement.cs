using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Forms = System.Windows.Forms;

namespace WindowsLookup.App;

/// <summary>按所在屏幕和 DPI 设置 WinUI 窗口的位置与尺寸。</summary>
internal static class WindowPlacement
{
    // 使用目标屏幕的工作区，位置和外框尺寸均按屏幕物理像素计算。
    internal static void _place(Window window, Point anchor, int width, int height, bool centered,
        Rectangle? selectionBounds = null)
    {
        Point monitorPoint = selectionBounds is { } bounds ? new Point(bounds.Right - 1, bounds.Bottom - 1) : anchor;
        Rectangle screen = Forms.Screen.FromPoint(monitorPoint).WorkingArea;
        AppWindow appWindow = window.AppWindow;
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Rectangle currentScreen = Forms.Screen.FromHandle(handle).WorkingArea;
        if (currentScreen != screen)
        {
            appWindow.Move(new PointInt32(screen.Left, screen.Top));
        }
        double scale = Math.Max(_getDpiForWindow(handle), 96) / 96.0;
        appWindow.ResizeClient(new SizeInt32(
            Math.Min(screen.Width, (int)Math.Round(width * scale)),
            Math.Min(screen.Height, (int)Math.Round(height * scale))));
        SizeInt32 size = appWindow.Size;
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
        appWindow.Move(new PointInt32(
            Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - size.Width)),
            Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height))));
    }

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint _getDpiForWindow(nint window);
}
