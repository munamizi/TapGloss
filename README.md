<p align="center">
  <img src="src/WindowsLookup.App/Assets/WindowsLookup.png" alt="TapGloss 图标" width="96" height="96">
</p>

<h1 align="center">TapGloss（轻触释义）</h1>

<p align="center">轻量的 Windows 托盘翻译工具，基于 WinUI 3 与原生 Win32 编写。</p>
<p align="center">选中文字后按 <kbd>Alt</kbd> + <kbd>T</kbd> 英译中，按 <kbd>Alt</kbd> + <kbd>R</kbd> 将中文或中英混合内容译为英文。</p>

## 安装

1. 在 x64 Windows 上安装 [.NET 10 Runtime（x64）](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) 和 [Windows App Runtime 2.6.5（x64）](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads#stable-release)。
2. 前往 [GitHub Releases](https://github.com/munamizi/TapGloss/releases) 的 **Assets** 下载 `TapGloss-win-x64.zip`，完整解压后把 `TapGloss` 文件夹放到常用目录，双击里面的 `TapGloss.exe` 启动。
3. 右键系统托盘图标，打开 **设置 API Key**，填入你自己的 DeepSeek API Key。

## 使用

在浏览器、编辑器等任意窗口选中文字后按下快捷键，译文会在选区或光标附近弹出，点击浮窗外部即可收起。翻译通过 DeepSeek 联网完成，单词朗读在本地离线完成。

- **Alt+T**：英文译简体中文。
- **Alt+R**：中文或中英混合内容译为英文，支持一键复制或直接替换原选区。

### 英译中（Alt+T）

**单词**：浮窗会显示中文释义和美式音标，并用本地 Amy 女声自动朗读一次，点击 **美式** 可重听。释义默认只保留**日常高频义**和**计算机常用义**，省略冷门义项；两类同时存在时，计算机义排在前面。

<p align="center">
  <img src="docs/lookup-demo.png" alt="单词翻译示例，含中文译文、美式音标和发音按钮" width="680">
</p>

**短语与句子**：浮窗直接展示中文译文。

<p align="center">
  <img src="docs/sentence-demo.png" alt="英译中句子翻译示例，浮窗只显示中文译文" width="680">
</p>

### 中译英（Alt+R）

支持中文单词、完整句子以及中英混排文本，译为自然英文的同时保留原有分段与代码标识符。

浮窗右上角提供**复制**与**替换选中文字**（剪刀图标）两个按钮：点击替换后，浮窗会自动收起并将英文粘贴回原输入框的选区，随后还原你之前的剪贴板内容（如果操作期间你复制了新内容，会保留新内容）。终端窗口则建议使用复制按钮。

<p align="center">
  <img src="docs/english-demo.jpg" alt="中译英翻译示例，浮窗显示英文译文和右上角复制按钮" width="680">
</p>

### 语音模式切换

右键系统托盘图标即可切换 **模式切换**。只要英译中热键可用，程序启动时就会在后台静默预热 Amy，两种模式控制的是闲置后是否释放模型内存：

| 模式 | 行为 |
| --- | --- |
| **动态续热（默认）** | 每次朗读后重新计时，闲置 15 分钟自动释放模型，下次朗读时再加载。 |
| **静态常驻** | 模型始终留在内存中，直到退出程序，适合高频查词、希望随时零延迟发音的场景。 |

## 架构

基于 C# / .NET 10（Windows x64）编写，界面采用 WinUI 3，托盘、热键、屏幕定位与剪贴板直接调用原生 Win32 API，不依赖 WinForms 或 WPF；发布版启用 ReadyToRun 预编译。

程序拆分为「主进程 + 两个子进程」隔离运行，子进程均托管在 Windows Job Object 下，主进程异常退出时由系统一并回收：

- **主进程**：监听热键、调度翻译与剪贴板、管理浮窗，并通过 Windows `waveOut` 播放音频；DeepSeek API Key 使用 Windows DPAPI 加密保存在本地。
- **选区子进程（`--selection-worker`）**：通过原生 COM UI Automation 3.0 读取选区文本与坐标，不支持时按安全规则降级为临时复制；超时可单独重启，闲置 15 分钟自动退出。
- **语音子进程（`--speech-worker`）**：基于 `sherpa-onnx` 运行本地 Amy 模型，经由仅限当前用户的命名管道返回音频；查词时并行预合成并缓存，按所选语音模式常驻或闲置释放。
