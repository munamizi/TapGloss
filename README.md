# TapGloss

基于 .NET 10 和 WinUI 3 的 Windows 托盘翻译工具。选中英文后按 **Alt+T**，鼠标附近会显示 DeepSeek 译文。单词支持美式音标和本地 Amy 朗读。

## 使用

完整解压应用运行包，在装有 x64 版 .NET 10 Desktop Runtime 和对应 Windows App Runtime 的 Windows 上运行 `TapGloss.exe`。右键托盘图标，打开「设置 API Key」保存自己的 DeepSeek API Key。密钥通过 Windows DPAPI 加密保存在当前用户的本地配置中。

托盘菜单中的「模式切换」可选择 Amy 动态续热或静态常驻。动态续热在闲置 15 分钟后释放模型，静态常驻会保留模型直到应用退出。

## 构建

在 Windows 上安装 .NET 10 SDK，从本目录打开 PowerShell。

```powershell
dotnet build .\src\WindowsLookup.App\WindowsLookup.App.csproj -c Release
```

发布可运行版本时，给 `PiperVoiceBundle` 指定完整的离线语音目录。其中应包含 Amy 的 ONNX 模型及配置、`python` 和 `packages`。可使用现有 TapGloss 运行包中的 `voice` 目录。

```powershell
dotnet publish .\src\WindowsLookup.App\WindowsLookup.App.csproj -c Release --self-contained false "-p:PiperVoiceBundle=C:\path\to\voice"
```