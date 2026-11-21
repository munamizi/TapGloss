using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Forms = System.Windows.Forms;

namespace WindowsLookup.App;

internal static class Program
{
    /// <summary>
    /// 分流选区与语音子进程、自检和单实例托盘入口。
    ///
    /// Args:
    ///     args - 托盘或内部工作模式的启动参数。
    /// Returns:
    ///     无返回值，语音子进程与发布自检会设置进程退出码。
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--selection-worker"])
        {
            Task.Run(SelectionReader.RunWorker).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--speech-selftest"])
        {
            Environment.ExitCode = AmericanSpeech.RunSelfTest();
            return;
        }
        if (args is ["--speech-worker", ..])
        {
            Environment.ExitCode = 2;
            if (args is [_, string pipe, string parent]
                && int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid)
                && parentPid > 0)
            {
                string prefix = $"TapGloss.Speech.{parentPid}.";
                if (pipe.StartsWith(prefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(pipe.AsSpan(prefix.Length), "N", out _))
                {
                    Environment.ExitCode = AmericanSpeech.RunWorker(pipe, parentPid);
                }
            }
            return;
        }

        using Mutex? instance = _claimInstance();
        if (instance is null)
        {
            Forms.MessageBox.Show("查词工具已在系统托盘运行。", "TapGloss", Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Information);
            return;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        LookupApplication? app = null;
        try
        {
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                app = new LookupApplication();
            });
        }
        finally
        {
            try
            {
                app?._disposeTray();
            }
            finally
            {
                OutsideClickWatcher._shutdownHookThread();
            }
        }
    }

    // 保留旧版使用的命名锁协议，给正在退出的实例留出短暂时间释放最后一个句柄。
    private static Mutex? _claimInstance()
    {
        long started = Stopwatch.GetTimestamp();
        do
        {
            var instance = new Mutex(initiallyOwned: false, @"Local\WindowsLookup", out bool isNew);
            if (isNew)
            {
                return instance;
            }
            instance.Dispose();
            Thread.Sleep(75);
        } while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(1500));
        return null;
    }
}

/// <summary>启动 WinUI 消息循环中的查词托盘。</summary>
public sealed partial class LookupApplication : Application
{
    private LookupTray? _tray;

    /// <summary>
    /// 加载 WinUI 的应用资源与默认控件样式。
    ///
    /// Args:
    ///     无。
    /// Returns:
    ///     LookupApplication - 即将启动托盘的应用。
    /// </summary>
    internal LookupApplication() => InitializeComponent();

    /// <summary>
    /// 创建托盘并注册查词热键。
    ///
    /// Args:
    ///     args - Windows 提供的启动信息
    /// Returns:
    ///     无返回值，托盘会开始接收热键消息。
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        _tray = new LookupTray();
    }

    // 在 UI 消息循环结束后释放托盘。
    internal void _disposeTray() => _tray?.Dispose();
}
