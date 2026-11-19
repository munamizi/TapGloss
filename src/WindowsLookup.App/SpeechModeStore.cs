using System.Diagnostics;

namespace WindowsLookup.App;

/// <summary>保存当前 Windows 用户选择的 Amy 内存模式。</summary>
internal sealed class SpeechModeStore
{
    private readonly string _path;

    /// <summary>
    /// 指定语音模式配置文件。
    ///
    /// Args:
    ///     path - 可选文件路径，省略时使用当前用户的本地配置目录。
    /// Returns:
    ///     SpeechModeStore - 用于读取和保存所选模式的存储。
    /// </summary>
    internal SpeechModeStore(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TapGloss", "speech-mode");

    // 没有配置、文件不可读或值未知时默认动态续热。
    internal SpeechMemoryMode _load()
    {
        try
        {
            return File.ReadAllText(_path).Trim() == "resident"
                ? SpeechMemoryMode.Resident
                : SpeechMemoryMode.DynamicWarmth;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return SpeechMemoryMode.DynamicWarmth;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Amy 模式配置读取失败（{0}）。", ex.GetType().Name);
            return SpeechMemoryMode.DynamicWarmth;
        }
    }

    // 新配置写成后再替换原文件，写入失败时保留原选择。
    internal void _save(SpeechMemoryMode mode)
    {
        string destination = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, mode == SpeechMemoryMode.Resident ? "resident" : "dynamic");
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("清理语音模式临时文件失败（{0}）。", ex.GetType().Name);
            }
        }
    }
}
