# Aurora

一个极简的 Windows 桌面音频播放器。窗口就是一块磁贴，封面取色染满整个界面。

![平台](https://img.shields.io/badge/平台-Windows%2011%20%7C%20x64-0078D4)
![框架](https://img.shields.io/badge/.NET-10-512BD4)
![UI](https://img.shields.io/badge/WinUI-3-0067B8)
![协议](https://img.shields.io/badge/协议-MIT-green)

---

## 系统要求

> **必须 Windows 11 24H2（Build 26100）或更高版本。**
> **不支持 Windows 10 及更早版本**，也不保证在 24H2 之前的 Windows 11 上运行。

| 项目 | 要求 |
|---|---|
| 操作系统 | **Windows 11 24H2（内部版本 26100）及以上** |
| 架构 | x64 |
| 运行库 | **无需另外安装** —— 安装包内已包含 .NET 10 与 Windows App SDK 运行时 |
| 磁盘 | 约 1.6 GB |

应用会在启动时检查系统版本，低于 26100 会直接给出提示并退出；安装程序也会在向导开始前拦截。

---

## 核心功能

- **封面取色** —— 从当前曲目的封面图提取主色调，实时渲染为界面的配色。
- **液态玻璃视觉** —— 半透明、模糊、随封面颜色流动的背景。
- **播放列表拖拽排序** —— 队列可自由拖拽重排，顺序即所见。
- **文件关联** —— 首次安装时可选择关联 13 种音频格式（mp3、flac、wav、m4a、aac、ogg、oga、opus、wma、aiff、aif、ape、mka），双击即播放。
- **命令行启动** —— 支持 `Aurora.exe "D:\Music\某首歌.flac"`，可直接打开指定文件。
- **动态进度条** —— 进度随播放推进，可拖拽跳转。
- **单实例** —— 再次打开文件时交给已在运行的窗口，不会启动第二个进程。
- **系统媒体控件** —— 接入 Windows 的系统媒体传输控件，支持键盘媒体键与耳机按钮。

---

## 安装

1. 打开本仓库的 **Releases** 页面。
2. 下载最新的 **`Aurora-1.5.1-Setup.exe`**。
3. 运行安装程序，按向导完成安装。

### 关于 SmartScreen 警告

安装程序**没有进行代码签名**，因此首次运行时 Windows 可能弹出拦截提示：

> **Windows 已保护你的电脑** —— Microsoft Defender SmartScreen 无法识别此应用。

这是未签名应用的正常表现，**不是安装程序本身有问题**。处理方式：

1. 点击提示窗口中的 **"更多信息"**。
2. 点击随后出现的 **"仍要运行"**。

如果下载后完全无法运行，可以右键安装包 → **属性** → 勾选底部的 **"解除锁定"** → 确定，然后重新运行。

> 若你希望彻底消除该提示，需要为可执行文件购买并配置受信任的代码签名证书。这属于分发方的工作，与使用者无关。

### 卸载

设置 → 应用 → 已安装的应用 → 找到 Aurora → 卸载。文件关联、快捷方式与注册表项会被一并清理。

---

## 构建

### 技术栈

| 组件 | 版本 |
|---|---|
| .NET | **10**（`net10.0-windows10.0.26100.0`） |
| UI 框架 | **WinUI 3**（Windows App SDK 2.5.1） |
| 播放内核 | LibVLCSharp 3.10.1 + libvlc 3.0.24 |
| 标签读取 | TagLibSharp 2.3.0 |

### 命令

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
  -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false
```

输出位于 `bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\`，约 1777 个文件、579 MB。

生成安装程序（需要 [Inno Setup 6.3+](https://jrsoftware.org/isdl.php)）：

```powershell
ISCC.exe installer\Aurora.iss
```

### 参数说明

| 参数 | 作用 | 省略的后果 |
|---|---|---|
| `WindowsPackageType=None` | 声明为未打包运行 | 应用要求包标识，直接运行 `Aurora.exe` 会启动失败 |
| `WindowsAppSDKSelfContained=true` | 捆绑 Windows App SDK 运行时 | 目标机必须预装 Windows App Runtime |
| `--self-contained true` | 捆绑 .NET 运行时 | 目标机必须预装 .NET 10 |
| **`PublishTrimmed=false`** | **关闭裁剪** | 见下方说明 |

### ⚠️ 必须关闭裁剪（`PublishTrimmed=false`）

**WinUI 3 不支持裁剪。** 开启后构建**不会报错**，而是静默产出一个损坏的应用 —— 这是本项目最容易踩的坑。

实测数据（裁剪 vs 正确）：

| 程序集 | 裁剪后（错误） | 正确大小 | 后果 |
|---|---|---|---|
| `TagLibSharp.dll` | 49,152 | **1,400,832** | 标签与时长读取依赖反射遍历各音频格式处理器，被裁掉后无法解析元数据 |
| `WinUIEx.dll` | 237,568 | **2,088,960** | 透明窗口背景失效 |
| `LibVLCSharp.dll` | 114,688 | **483,328** | 播放绑定受损 |

构建期会出现 `IL2072`（打在文件选择器的 COM 激活上）与 `IL2104`（打在 `WinRT.Runtime`、`Microsoft.Windows.SDK.NET`、`TagLibSharp` 上）——**这些都不会变成错误**，必须人工确认。

`Aurora.csproj` 中已无条件写入 `<PublishTrimmed>False</PublishTrimmed>`，不按配置区分。

发布后自检：

```powershell
$pub = 'bin\Release\net10.0-windows10.0.26100.0\win-x64\publish'
(Get-ChildItem $pub -Recurse -File | Measure-Object).Count   # 期望 1777
Test-Path "$pub\Aurora.runtimeconfig.json"                   # 必须 True
Test-Path "$pub\Aurora.deps.json"                            # 必须 True
Test-Path "$pub\Aurora.pri"                                  # 必须 True
```

文件数明显少于 1777，或上面三个文件缺任何一个，说明发布目录不完整。

---

## 已知行为

### 切歌时句柄数量缓慢增长 —— **属内核对象缓存，不是内存泄漏**

连续切歌时，进程的句柄数会缓慢上升（约 **+0.3 ~ +0.7 个/次**，取决于切歌速度）。**这是正常的**：

- **静置后会回落。** 停止操作约 60–90 秒后，句柄数明显下降并趋于平稳（实测 400 次切歌后峰值 2,439，静置 90 秒回落至 2,175）。
- **内存全程平稳。** 400 次切歌私有内存仅增长约 2 MB。若真的在泄漏原生对象，内存会同步上涨。
- **线程数同步回落**（57 → 37），同属"按需扩张、空闲回收"。

libvlc 每次打开媒体都会新建与本文件相关的内核对象（文件句柄、文件映射等），这些对象按需回收而非立即释放。**无需处理，也不建议调整释放时机** —— 强制提前释放会在 libvlc 仍有未决操作时抽走媒体，导致崩溃。

### 启动比预期慢

启动时先显示一张启动卡片，播放器在卡片背后构建，构建完成后卡片揭幕。整个过程通常约 5 秒，机器负载较高时会稍长。**这是设计行为**，不是卡死。

---

## 开源协议

本项目使用 **MIT 协议**，详见 [LICENSE](LICENSE)。

```
MIT License
Copyright (c) 2026 Darcy
```

允许自由使用、修改、分发与商用，保留版权声明与许可声明即可。

---

## 致谢

播放能力来自 [VideoLAN](https://www.videolan.org/) 的 libVLC 与 [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp)；
界面基于 [WinUI 3](https://learn.microsoft.com/windows/apps/winui/winui3/) 与 [WinUIEx](https://github.com/dotMorten/WinUIEx)；
标签解析使用 [TagLibSharp](https://github.com/mono/taglib-sharp)。
