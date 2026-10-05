# OTAKUsVget

基于 [yt-dlp](https://github.com/yt-dlp/yt-dlp) 的视频下载器（WinUI 3 / .NET 8），主打哔哩哔哩，内置 yt-dlp / aria2c / ffmpeg，开箱即用。

> 仅供个人学习与合法用途，请勿用于侵权或再分发。

## 功能

- 扫码 / 密码 / 短信登录，支持高清、大会员内容
- 番剧整季「选集」、视频分P、合集，一键选择单集 / 全部
- 仅音频下载（mp3 / m4a / flac）
- 清晰度与编码选择（H.264 / H.265 / AV1），「自动」按预估体积选择
- 解析加速：B站链接先走官方轻量 API 出卡片，清晰度用 wbi playurl 秒出；输入即预取 + 短期缓存
- 下载页分组（进行中 / 已完成）、断点续传、同时下载数、历史记录分类
- 主题跟随系统 / 背景材质 / 强调色；封面缓存；封面可保存为 JPG / PNG

## 环境

- Windows 10 1809+ / Windows 11（x64）
- 开发：.NET SDK 8.0、Windows App SDK 1.6
- 运行（自包含发布）：无需额外安装

## 构建

```powershell
# 自包含发布（输出到 bin\Release\...\win-x64\publish）
dotnet publish BiliGet.csproj -c Release -r win-x64
```

发布前先获取内置工具（不随仓库提交）：

```powershell
# 下载 yt-dlp.exe / aria2c.exe / ffmpeg.exe 到 tools\
powershell -ExecutionPolicy Bypass -File scripts\fetch-tools.ps1
```

## 目录

- 根目录：WinUI 3 应用（C# / XAML）
  - `Bili.cs`：B站 API（登录、结构、wbi 签名、playurl）
  - `YtDlp.cs`：yt-dlp / aria2c 调用与输出解析、封面缓存
  - `MainWindow.xaml(.cs)`：界面与解析 / 下载流程
- `scripts/fetch-tools.ps1`：获取内置工具

## 致谢

- [yt-dlp](https://github.com/yt-dlp/yt-dlp)、[aria2](https://github.com/aria2/aria2)、[FFmpeg](https://ffmpeg.org/)
- [QRCoder](https://github.com/codebude/QRCoder)、[Windows App SDK](https://github.com/microsoft/WindowsAppSDK)、[.NET](https://github.com/dotnet/runtime)
- wbi 签名与部分接口参考 [bilibili-API-collect](https://github.com/SocialSisterYi/bilibili-API-collect)

## 许可

见 [LICENSE](LICENSE)。
