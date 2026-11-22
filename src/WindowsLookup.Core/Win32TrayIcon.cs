using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsLookup;

/// <summary>用内置 ICO 数据创建由调用方释放的原生托盘图标。</summary>
internal static class Win32TrayIcon
{
    // 将 ICO 偏移表转换成 Win32 可选帧的组资源目录，再加载选中的图标帧。
    internal static nint _load(byte[] ico)
    {
        const int directorySize = 6, fileEntrySize = 16, groupEntrySize = 14;
        if (ico.Length < directorySize || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1)
        {
            throw new InvalidDataException("内置 ICO 文件头无效。");
        }

        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        int frameStart = directorySize + count * fileEntrySize;
        if (count == 0 || frameStart > ico.Length)
        {
            throw new InvalidDataException("内置 ICO 缺少完整帧目录。");
        }

        var group = new byte[directorySize + count * groupEntrySize];
        ico.AsSpan(0, directorySize).CopyTo(group);
        for (int index = 0; index < count; index++)
        {
            int frameEntry = directorySize + index * fileEntrySize;
            int groupEntry = directorySize + index * groupEntrySize;
            ico.AsSpan(frameEntry, 12).CopyTo(group.AsSpan(groupEntry));
            BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(groupEntry + 12), (ushort)(index + 1));
        }

        int width = Math.Max(1, _getMetric(49));
        int height = Math.Max(1, _getMetric(50));
        int id = _selectFrame(group, true, width, height, 0);
        if (id < 1 || id > count)
        {
            throw new InvalidDataException("无法选出内置 ICO 图标帧。");
        }
        var frame = ico.AsSpan(directorySize + (id - 1) * fileEntrySize, fileEntrySize);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(frame[8..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(frame[12..]);
        if (length == 0 || offset < frameStart || (ulong)offset + length > (ulong)ico.Length)
        {
            throw new InvalidDataException("内置 ICO 图标帧超出文件边界。");
        }

        byte[] image = ico.AsSpan((int)offset, (int)length).ToArray();
        nint icon = _create(image, length, true, 0x00030000, width, height, 0);
        return icon != 0 ? icon : throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    // 释放通过图标资源数据新建的句柄。
    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool _destroy(nint icon);

    // 按 Windows 小图标尺寸选择匹配的图标资源。
    [DllImport("user32.dll", EntryPoint = "LookupIconIdFromDirectoryEx", SetLastError = true)]
    private static extern int _selectFrame(byte[] directory, [MarshalAs(UnmanagedType.Bool)] bool icon,
        int width, int height, uint flags);

    // 使用选定帧的位图数据创建原生图标。
    [DllImport("user32.dll", EntryPoint = "CreateIconFromResourceEx", SetLastError = true)]
    private static extern nint _create(byte[] data, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon,
        uint version, int width, int height, uint flags);

    // 读取系统小图标的高宽。
    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int _getMetric(int index);
}
