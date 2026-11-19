using System.Security.Cryptography;
using System.Text;

namespace WindowsLookup;

/// <summary>把 API Key 加密保存到当前 Windows 用户的本地配置。</summary>
public sealed class DeepSeekKeyStore
{
    private readonly string _path;

    /// <summary>
    /// 确定 API Key 的保存文件。
    ///
    /// Args:
    ///     path - 可选绝对文件路径，省略时使用当前用户的 LocalAppData
    /// Returns:
    ///     DeepSeekKeyStore - 对应保存路径的密钥存储
    /// </summary>
    public DeepSeekKeyStore(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsLookup", "deepseek.key");

    /// <summary>
    /// 读取并解密当前用户保存的 API Key。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     API Key - 未保存时为 null，文件损坏或当前用户无法解密时抛出异常。
    /// </summary>
    public string? Load()
    {
        byte[] protectedKey;
        try
        {
            protectedKey = File.ReadAllBytes(_path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        byte[] plaintext = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// 加密 API Key 并替换配置文件。
    ///
    /// Args:
    ///     apiKey - 用户提供的 DeepSeek API Key
    /// Returns:
    ///     无返回值，同目录临时文件写成后才替换原配置。
    /// </summary>
    public void Save(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        byte[] plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        string destination = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
