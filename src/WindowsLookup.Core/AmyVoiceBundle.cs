using System.IO;

namespace WindowsLookup;

/// <summary>集中检查 Amy 模型数据和 Windows x64 原生库。</summary>
internal static class AmyVoiceBundle
{
    internal const string Model = "en_US-amy-medium.onnx";
    internal const string Tokens = "tokens.txt";
    internal const string EspeakData = "espeak-ng-data";
    private static readonly string[] ModelFiles =
    [
        Model, Tokens, EspeakData + "/phontab", EspeakData + "/phonindex",
        EspeakData + "/phondata", EspeakData + "/intonations",
    ];
    private static readonly string[] NativeFiles = ["sherpa-onnx.dll", "sherpa-onnx-c-api.dll", "onnxruntime.dll"];

    // 返回第一个缺失或为空的发布资源的相对路径。
    internal static string? _findMissing(string directory)
    {
        foreach (string file in ModelFiles)
        {
            string relative = "voice/" + file;
            if (!_hasData(Path.Combine(directory, relative)))
            {
                return relative;
            }
        }
        return NativeFiles.FirstOrDefault(file => !_hasData(Path.Combine(directory, file)));
    }

    // 子进程在切换工作目录前检查所有模型文件及原生依赖。
    internal static void _requireComplete(string directory)
    {
        if (_findMissing(directory) is { } file)
        {
            throw new SpeechException(SpeechFailureKind.BundleIncomplete, $"Amy 缺少文件：{file}。");
        }
    }

    // 以实际长度区分空占位文件和可加载资源。
    private static bool _hasData(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length > 0;
    }
}
