using System.Diagnostics;
using System.IO;
using System.Text;

namespace WindowsLookup;

/// <summary>区分语音资源、子进程、合成和播放错误。</summary>
public enum SpeechFailureKind
{
    BundleIncomplete,
    WorkerStartFailed,
    SynthesisTimeout,
    SynthesisFailed,
    AudioDeviceFailed,
}

/// <summary>携带可供界面分类并可安全输出到诊断日志的语音错误。</summary>
public sealed class SpeechException : Exception
{
    public SpeechFailureKind Kind { get; }
    public int? Code { get; }
    internal bool Retryable { get; }

    /// <summary>
    /// 创建包含阶段分类和原生错误码的语音错误。
    ///
    /// Args:
    ///     kind - 对应界面提示的故障类别。
    ///     message - 程序提供的错误说明。
    ///     code - 可用的 MMRESULT、HRESULT 或退出码。
    ///     inner - 底层异常，用于保留调用栈。
    ///     retryable - 是否需要重试本次语音操作。
    /// Returns:
    ///     SpeechException - 可由托盘或发布自检处理的语音异常。
    /// </summary>
    internal SpeechException(SpeechFailureKind kind, string message, int? code = null,
        Exception? inner = null, bool retryable = false) : base(message, inner)
    {
        Kind = kind;
        Code = code;
        Retryable = retryable;
    }
}

internal static class SpeechDiagnostics
{
    internal const int MaxLogBytes = 256 * 1024;
    private static readonly object LogLock = new();
    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapGloss", "logs");

    // 只记录阶段、分类和数字错误码，原始异常与待朗读正文不进入日志。
    internal static void _record(string stage, SpeechFailureKind kind, int? code = null,
        IReadOnlyCollection<string>? nativeEvents = null) =>
        _write(LogDirectory, stage, kind, code, nativeEvents);

    // 按 UTF-8 写入一条记录并在达到上限前轮换，只保留一份旧日志。
    internal static void _write(string directory, string stage, SpeechFailureKind kind, int? code = null,
        IReadOnlyCollection<string>? nativeEvents = null)
    {
        string native = nativeEvents is { Count: > 0 } ? string.Join(",", nativeEvents) : "-";
        string line = $"{DateTimeOffset.UtcNow:O} stage={stage} kind={kind} code={code?.ToString() ?? "-"} native={native}{Environment.NewLine}";
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "speech.log");
                if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes - Encoding.UTF8.GetByteCount(line))
                {
                    File.Move(path, path + ".old", overwrite: true);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("{0} {1} {2}", stage, kind, code);
        }
    }

    // 将原生 stderr 转成固定的分类标签，避免把模型输出、词句或路径写入日志。
    internal static string _classifyNativeLine(string line)
    {
        if (line.Contains("espeak", StringComparison.OrdinalIgnoreCase))
        {
            return "espeak-ng";
        }
        if (line.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
        {
            return "onnxruntime";
        }
        return line.Contains("sherpa", StringComparison.OrdinalIgnoreCase) ? "sherpa-onnx" : "native-stderr";
    }
}
