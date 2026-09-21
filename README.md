# MobileSDViewer

Windows 上通过 ADB 浏览、管理 Android 设备文件的小工具。看目录、传文件、预览图片/文本，也可以把 APK 装到当前设备。

需要本机有 `adb.exe`（Android SDK platform-tools）。程序会按 上次路径 → PATH → `ANDROID_HOME` / `ANDROID_SDK_ROOT` → 常见 SDK 目录 自动查找；找不到时在顶栏手动指定。

## 用法

1. 手机打开 **USB 调试** 并授权，或填无线调试 `IP:端口` 后点「连接」。
2. 在顶栏选择设备。状态必须是 `device`；若是 `unauthorized`，在手机上点允许。
3. 左侧目录树、中间文件列表、右侧预览。双击文件夹进入，路径栏或面包屑可跳转。

快捷入口：**SD卡**、**Download**、**DCIM**、**Android/data**、**本游戏**（`com.shengqugames.shelterslg.debug`）。

| 操作 | 说明 |
|------|------|
| 上传 / 下载 | 上传到当前目录；下载选中项，未选中则下当前文件夹。文件列表支持拖入本地文件上传 |
| 安装 APK | 选本地 `.apk`，对当前设备执行 `adb install -r`（覆盖同包名） |
| 新建 / 重命名 / 删除 | 文件列表右键 |
| 预览 | 图片（png/jpg/gif/bmp/webp）与常见文本；文本可 Ctrl+F 搜索 |

访问 `Android/data` 受限时，会尝试 `run-as`（仅 debuggable 包）或 `su`。

## 从源码编译

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（含 Windows Desktop）。

```text
dotnet run --project MobileSDViewer.csproj
```

发布单文件：

```text
dotnet publish MobileSDViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```

## 许可

仅用于项目内工具分发。未单独指定开源许可证。
