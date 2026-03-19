# ServerChanDesktop

一个基于 WPF 的 Windows 桌面收件箱客户端，用来读取 Server3 的消息列表，并以本地桌面应用的形式查看、筛选和管理消息。

## 功能

- 使用 `SendKey` 登录收件箱
- 同步 Server3 消息列表
- 本地查看消息详情
- 支持未读、加星、关键词搜索
- 支持图片消息显示
- 支持桌面弹窗提醒
- 支持托盘驻留

## 项目结构

```text
ServerChanDesktop/
  App.xaml
  MainWindow.xaml
  MainWindow.xaml.cs
  NotificationWindow.xaml
  NotificationWindow.xaml.cs
  Assets/
    app.ico
```

## 运行环境

- Windows 10 / 11
- .NET 9 SDK

## 本地构建

```powershell
dotnet build .\ServerChanDesktop\ServerChanDesktop.csproj
dotnet publish .\ServerChanDesktop\ServerChanDesktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 发布版

当前仓库外的本地发布产物路径示例：

```text
publish-release/ServerChanDesktop.exe
```

## 说明

- 本项目当前以桌面收件能力为主，不包含完整移动端能力复刻。
- 一些状态会在桌面端本地维护，以便提供更适合桌面使用的交互体验。

## License

暂未指定。
