using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsLookup;

[Flags]
public enum HotkeyModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
}

/// <summary>接收全局热键并记录触发瞬间的前台窗口。</summary>
public sealed class WindowsHotkey : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const int HotkeyMessage = 0x0312;
    private const uint NoRepeat = 0x4000;

    /// <summary>
    /// 在调用线程的消息循环上注册全局快捷键。
    ///
    /// Args:
    ///     modifiers - 快捷键修饰键
    ///     key - 主键，默认 T
    /// Returns:
    ///     WindowsHotkey - 已注册热键的消息窗口，冲突时抛出 Win32Exception
    /// </summary>
    public WindowsHotkey(HotkeyModifiers modifiers = HotkeyModifiers.Alt, Keys key = Keys.T)
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        if (!_registerHotKey(Handle, HotkeyId, (uint)modifiers | NoRepeat, (uint)key))
        {
            int error = Marshal.GetLastPInvokeError();
            DestroyHandle();
            throw new Win32Exception(error);
        }
    }

    /// <summary>在热键注册线程通知订阅者，并传入当时的前台窗口句柄。</summary>
    public event Action<nint>? Pressed;

    /// <summary>
    /// 在注册线程释放热键和消息窗口。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     无返回值。
    /// </summary>
    public void Dispose()
    {
        if (Handle != 0)
        {
            _unregisterHotKey(Handle, HotkeyId);
            DestroyHandle();
        }
    }

    /// <summary>
    /// 转发注册的热键消息。
    ///
    /// Args:
    ///     message - Windows 传给该消息窗口的消息
    /// Returns:
    ///     无返回值，收到注册热键时触发 Pressed。
    /// </summary>
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == HotkeyMessage && message.WParam == HotkeyId)
        {
            Pressed?.Invoke(_getForegroundWindow());
            return;
        }
        base.WndProc(ref message);
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    internal static extern nint _getForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    internal static extern uint _getWindowThreadProcessId(nint window, out uint processId);

    /// <summary>
    /// 取得来源窗口线程当前的原生焦点控件。
    ///
    /// Args:
    ///     sourceWindow - 本次热键触发时的前台窗口句柄。
    /// Returns:
    ///     焦点控件句柄 - 欠缺窗口或无法取得焦点时为 0。
    /// </summary>
    public static nint GetFocusedWindow(nint sourceWindow)
    {
        uint thread = _getWindowThreadProcessId(sourceWindow, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && _getGuiThreadInfo(thread, ref info) ? info.Focus : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal int Size;
        internal int Flags;
        internal nint Active;
        internal nint Focus;
        internal nint Capture;
        internal nint MenuOwner;
        internal nint MoveSize;
        internal nint Caret;
        internal int CaretLeft;
        internal int CaretTop;
        internal int CaretRight;
        internal int CaretBottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _getGuiThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    private static extern bool _registerHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    private static extern bool _unregisterHotKey(nint window, int id);
}
