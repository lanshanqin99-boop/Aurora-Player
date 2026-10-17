# 扩展开发指南

面向想在这个项目之上继续开发的人。本文只讲扩展点与需要注意的边界，不重复构建与环境配置
（那部分见仓库根目录的 [README](../README.md)）。

---

## 一、引入 `LibVLCSharp.WinUI`

Aurora 目前只用到 `LibVLCSharp`（核心绑定）与 `VideoLAN.LibVLC.Windows`（libvlc 二进制）。
渲染到 WinUI 控件需要额外的 `LibVLCSharp.WinUI` 包，它是 `VideoView` 控件所在的位置。

### 1. 添加包引用

```powershell
dotnet add package LibVLCSharp.WinUI
```

> 版本请与本项目已有 `LibVLCSharp` 的版本保持一致（当前 3.10.1），避免核心绑定与 UI 包之间
> 出现 ABI 不匹配。**升级依赖版本会影响播放核心，不属于扩展范畴。**

### 2. 确认 libvlc 二进制仍在位

`VideoLAN.LibVLC.Windows` 会随构建把 libvlc 复制到输出目录的 `libvlc\win-x64\`。视频渲染依赖
同一份二进制，**不需要额外安装**。发布后请确认：

```powershell
Test-Path 'libvlc\win-x64\libvlc.dll'
```

### 3. 注册命名空间

在需要用到 `VideoView` 的 XAML 页面顶部加入（`LibVLCSharp.WinUI` 的命名空间前缀按你实际引用的
包版本填写，通常在包的 README 中会给出）：

```xml
xmlns:vlc="using:LibVLCSharp.Platforms.Windows"
```

---

## 二、创建 `VideoView` 并让它显示当前播放内容

### ⚠️ 先说明一个现状限制

**`PlaybackService` 目前没有公开它内部的 `MediaPlayer`。** 类中只有：

```csharp
private readonly MediaPlayer _mediaPlayer;      // PlaybackService.cs
```

这是刻意的封装：播放状态、队列、时长解析、单实例都绕这一个实例转，外部拿不到它就无法把
`MediaPlayer` 置于不一致的状态。所以**不能**写出这样的代码 —— 它编译不过：

```csharp
// ✗ 不行：PlaybackService 没有公开 MediaPlayer 属性
videoView.MediaPlayer = playbackService.MediaPlayer;
```

要接上视频画面，有两条路，**都需要改动 `PlaybackService`**。

### 方案 A —— 只读地暴露同一个实例（推荐）

在 `PlaybackService` 中增加一个**只读属性**，让扩展代码拿到的是同一个播放器，而不是新开一个：

```csharp
/// <summary>
/// The player the service drives. Exposed read-only: callers may hand it to a renderer, but must not
/// load media into it or change its state — the queue and the current index would no longer agree
/// with what is on screen.
/// </summary>
public MediaPlayer MediaPlayer => _mediaPlayer;
```

扩展窗口里：

```csharp
public sealed partial class VideoWindow : Window
{
    private readonly PlaybackService _playback;

    public VideoWindow(PlaybackService playback)
    {
        InitializeComponent();

        _playback = playback;

        // The same MediaPlayer the audio is already playing through: attaching it to a view does not
        // restart, stop or re-load anything, it only gives the picture somewhere to go.
        Video.Source = _playback.MediaPlayer;
    }
}
```

```xml
<Window x:Class="Aurora.VideoWindow" ...>
    <Grid>
        <vlc:VideoView x:Name="Video" />
    </Grid>
</Window>
```

**约束**：拿到这个实例的代码**只读**。不要调 `Play(media)`、`Stop()`、`Media = ...`，也不要订阅并
修改状态 —— 队列、当前索引、`IsCurrentTrackLoaded` 三者的同步全部由 `PlaybackService` 独占维护。

### 方案 B —— 独立播放器（完全隔离）

如果你要的是"视频和音频互不干扰"，就**不要**复用 Aurora 的播放器，自己建一个：

```csharp
Core.Initialize();                     // 与 PlaybackService 共用同一份 libvlc 二进制
var libvlc = new LibVLC();
var player = new MediaPlayer(libvlc);
Video.Source = player;
```

这条路的代价是失去与主界面的联动（进度、封面取色、系统媒体控件都不再一致），但完全不入侵
现有播放核心。**若你的目标是"能放视频"，方案 B 更安全。**

---

## 三、添加视频播放时，请新建独立窗口

**Aurora 的主界面是一块磁贴**：400×200 的固定比例窗口，配合启动卡片揭幕、液态玻璃背景、
封面取色、以及一条与播放列表窗口共同覆盖的半透明画面。这个形态是整套视觉的基础 ——
尺寸、圆角、模糊层、取色映射、z-order 层带全部围绕它设计。

因此：

> **不要把 `VideoView` 放进主窗口。** 视频画面会撑破磁贴形态，破坏取色与模糊层，
> 并让窗口尺寸逻辑（含 `holdForReveal` 揭幕流程）失效。

正确做法是**新建一个独立顶层窗口**承载视频：

- 用 `WinUIEx.WindowEx` 派生一个 `VideoWindow`，与现有 `PlaylistWindow` 相同的窗口管理方式
  （`SetWindowLongPtr(handle, GWLP_HWNDPARENT, ownerHandle)` 设所有者，而不是设父窗口）。
- 主窗口通过一个按钮打开它，**主窗口自身的布局与尺寸不要改**。
- 关闭视频窗口时像 `PlaylistWindow` 一样把字段置 `null` 并**退订所有事件**。

关于最后这条，项目里有一个已经踩过的坑值得记住：`PlaylistWindow` 曾在自己的 XAML 元素上
订阅了一个 lambda（`Shell.SizeChanged += (_, _) => ...`），而 lambda 捕获了窗口自身，
导致关闭的窗口永远无法被回收 —— 实测 60 次开关后有 31 个已关闭窗口仍留在堆上。
**在元素或服务上订阅事件时，必须使用具名方法，并在 `Closed` 中成对退订。**

---

## 四、其他扩展点

| 需求 | 落点 | 注意事项 |
|---|---|---|
| 支持新的音频格式 | `PlaybackService.SupportedExtensions` | 同时要在 `installer\Aurora.iss` 的 `[Registry]` 段补一条关联 |
| 新的封面取色算法 | `PaletteExtractor.cs` / `CoverPaletteService.cs` | 输出需满足 `AmbientPalette` 的约束，否则取色会过饱和 |
| 新的进度条动效 | `MainWindow.xaml.cs` 的进度刷新路径 | 不要改 `Position` 的语义，`CanSeek` 依赖它 |
| 新的排序规则 | `AudioTrack.DefaultOrder` / `TrackGrouping.cs` | 播放列表按标题排序，新增规则需保持稳定排序 |

---

## 五、提交前自检

项目自带一组规格测试（`CanvasSpecs`、`GroupSpecs`、`DisplaySpecs`、`SortSpecs`、`QueueSpecs`、
`LogPathSpecs`、`PaletteProbe`），共 128 项。改动后请全部跑通：

```powershell
dotnet run --project QueueSpecs\QueueSpecs.csproj
```

构建必须保持 **0 警告 0 错误**，并且发布时 **`PublishTrimmed=false`**（原因见 README）。
