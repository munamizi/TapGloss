<p align="center">
  <img src="src/WindowsLookup.App/Assets/WindowsLookup.png" alt="TopGloss 图标" width="96" height="96">
</p>

<h1 align="center">TopGloss（轻触释义）</h1>

<p align="center">轻量的 Windows 托盘翻译工具。选中英文，按 <kbd>Alt</kbd> + <kbd>T</kbd>，就能看到英文简中译文。</p>

# 说明

## 安装

1. 在 x64 Windows 上安装 [.NET 10 Desktop Runtime（x64）](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) 和 [Windows App Runtime 2.6.5（x64）](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads#stable-release)。
2. 在 [GitHub Releases](https://github.com/munamizi/TapGloss/releases) 的 **Assets** 下载 `TapGloss-win-x64.zip`。完整解压后会得到一个 `TapGloss` 文件夹，把整个文件夹放到常用位置，然后双击里面的 `TapGloss.exe`。
3. 右键系统托盘中的应用图标，打开 **设置 API Key**，填入自己的 DeepSeek API Key。

如果要查看或修改代码，可下载 `TapGloss-source.zip`。它用于源码开发，需要 .NET 10 SDK。构建完整运行版时，可把运行包里的 `voice` 目录传给项目的 `PiperVoiceBundle` 参数。

## 使用

在浏览器或编辑器里选中英文，按 **Alt+T**。译文会在选区或光标附近弹出，点击浮窗外即可收起。

**单词**。浮窗显示中文译文和可用的美式音标，并用本地 Amy 女声自动朗读一次。点击 **美式** 可以重听。

单词默认优先给出**日常高频释义**和**计算机领域常用释义**，省略冷门释义。如果两类都有，计算机义排在前面。

<p align="center">
  <img src="docs/lookup-demo.png" alt="单词翻译示例，含中文译文、美式音标和发音按钮" width="680">
</p>

**短语或句子**。浮窗只显示中文译文。

<p align="center">
  <img src="docs/sentence-demo.png" alt="句子翻译示例，浮窗只显示中文译文" width="680">
</p>

翻译通过 DeepSeek 联网完成，Amy 朗读在本机离线完成。

### 模式切换

右键系统托盘图标，选择 **模式切换**。两种模式都会在程序启动时静默预热 Amy，区别在于闲置后是否释放语音模型。

| 模式 | 行为 |
| --- | --- |
| **动态续热（默认）** | 每次朗读后重新计时。闲置 15 分钟后释放模型，下次朗读时重新加载。 |
| **静态常驻** | 模型一直留在内存里，直到退出程序。适合频繁查词、希望随时快速发音的场景。 |

## 架构说明

TopGloss 用 C# 和 .NET 10 编写，托盘菜单和译文浮窗使用 WinUI 3。按下 **Alt+T** 后，程序按以下流程处理。

1. **读取选区**。用 Windows UI Automation 读取当前应用选中的文字。遇到不支持直接读取的应用，会尝试通过临时复制取得选区。
2. **翻译并显示**。将取得的文字交给 DeepSeek，收到简体中文译文后，在选区或光标附近显示浮窗。
3. **本地朗读**。如果查的是单词，程序会把英文单词交给本机的 Piper 语音进程，由 Amy 合成发音并播放。
