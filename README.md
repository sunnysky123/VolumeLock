# VolumeLock

VolumeLock 是一个基于 WinUI 3 的 Windows 音量锁定工具，用于固定扬声器和麦克风的系统音量，避免部分软件在后台擅自调整音量。

<p align="center">
  <img src="Assets/AppIcon.png" width="128" alt="VolumeLock 图标" />
</p>

<p align="center">
  <a href="https://apps.microsoft.com/detail/9P0GM98HNNQD">Microsoft Store</a> ·
  <a href="PRIVACY.md">隐私声明</a> ·
  <a href="LICENSE">MIT License</a>
</p>

## 下载安装

VolumeLock 已上架 Microsoft Store，可直接下载安装使用，无需自行构建：

<p align="center">
  <a href="https://apps.microsoft.com/detail/9P0GM98HNNQD">
    <img src="https://get.microsoft.com/images/zh-CN%20dark.svg" width="200" alt="从 Microsoft Store 获取" />
  </a>
</p>

商店页面：<https://apps.microsoft.com/detail/9P0GM98HNNQD>

安装后可从开始菜单或搜索中启动 VolumeLock，后续更新由 Microsoft Store 自动推送。

## 界面截图

<p align="center">
  <img src="screenshot/main.png" alt="VolumeLock 主界面截图" />
</p>

## 功能

- 枚举并选择当前启用的扬声器（输出设备）和麦克风（输入设备）
- 读取、设置所选设备的系统主音量
- 分别锁定扬声器和麦克风音量
- 通过 Core Audio 音量变更回调和定时校验，自动恢复被其他程序修改的音量
- 显示已拦截的外部修改次数
- 支持系统托盘：
  - 左键点击托盘图标显示主界面
  - 右键菜单可显示主界面、锁定/解锁扬声器、锁定/解锁麦克风、退出
  - 菜单勾选状态与主界面锁定状态实时同步
  - 任意设备处于锁定状态时显示正常图标，全部解锁时显示灰色解锁图标
  - 托盘提示中显示当前锁定的设备和音量
- 关闭主窗口时隐藏到托盘，应用继续在后台保持锁定
- 单实例运行：重复启动时激活已有窗口
- 跟随系统浅色/深色主题，使用 Mica 背景
- 内置简体中文和英文界面

## 系统要求

运行（从 Microsoft Store 安装）：

- Windows 10 版本 1809（build 17763）或更高版本
- .NET 11 桌面运行时（Microsoft.WindowsDesktop.App 11.x）。应用以**框架依赖**（framework-dependent）方式发布，不内置 .NET 运行时；未安装时启动会提示下载。

从源码构建本项目还需要：

- .NET 11 SDK
- 首次还原 NuGet 包时需要网络连接
- 推荐使用 Visual Studio 2022 或更高版本，并安装“Windows 应用开发”相关负载

## 从源码构建

> 仅在你需要自行编译或修改代码时需要本节内容；普通用户请直接从 [Microsoft Store](https://apps.microsoft.com/detail/9P0GM98HNNQD) 安装。

在项目根目录执行：

```powershell
dotnet restore VolumeLock.csproj
dotnet build VolumeLock.csproj -c Debug -p:Platform=x64
```

也可以使用 Visual Studio 打开 `VolumeLock.slnx`，选择 `x64` 平台后生成。

项目支持以下平台：

- `x64`
- `ARM64`

## 从源码运行

```powershell
dotnet run --project VolumeLock.csproj -p:Platform=x64
```

如果使用 Visual Studio，直接按 `F5` 启动即可。

## 图标资源

所有磁贴、启动画面、商店徽标和托盘图标都由品牌母版 [`Assets/AppIcon.png`](Assets/AppIcon.png) 统一派生，不要手工编辑单个尺寸的文件。修改母版后执行：

```powershell
.\Tools\Generate-Assets.ps1
```

脚本会重新生成 `Assets` 下的全部 59 个资源文件（含多语言缩放变体），并清理本脚本历史版本遗留的过时文件：

| 资源 | 生成的变体 |
| --- | --- |
| `Square44x44Logo` | 默认 + `scale-125/150/200/400` + 14 组 `targetsize-*_altform-unplated/-lightunplated` |
| `Square150x150Logo` | 默认 + `scale-125/150/200/400` |
| `Wide310x150Logo` | 默认 + `scale-125/150/200/400` |
| `SplashScreen` | 默认 + `scale-125/150/200/400` |
| `StoreLogo` | 默认 + `scale-125/150/200/400` |
| `LockScreenLogo` | `scale-100/200/400`（纯白单色，锁屏背景上可见） |
| `AppIcon.ico` / `AppIconUnlocked.ico` | 16/20/24/32/40/48/64/128/256 px 多尺寸 |

注意：

- 磁贴类资源使用不透明渐变底 + 白色锁形，浅色和深色主题下都能看清。
- 启动画面为透明底，背景色由 `Package.appxmanifest` 的 `SplashScreen` `BackgroundColor` 提供。
- ICO 必须保留 16×16 条目，否则托盘图标会被缩放模糊。

## 打包

生成 x64 MSIX：

```powershell
dotnet publish VolumeLock.csproj -c Release `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=false
```

生成 x64 + ARM64 商店捆绑包：

```powershell
.\Build-StoreBundle.ps1
```

脚本会先调用 `Tools\Generate-Assets.ps1` 重建图标资源，然后校验清单引用的图标文件全部存在、ICO 含 16×16 条目，最后产出包含 x64/ARM64 主包与 `scale-100/125/150/400` 资源包的捆绑包。输出目录为 `StorePackages`。

### 部署方式

项目以 **.NET 框架依赖**（`<SelfContained>false</SelfContained>`）方式发布：MSIX 内不含 .NET 运行时，安装包按架构约 14 MB。

需要注意的行为：Microsoft Store 只会根据清单中的 `<PackageDependency>` 自动安装“框架包”（本项目只有 Windows App SDK 的 `Microsoft.WindowsAppRuntime.2`）。.NET 5+ 没有对应的 MSIX 框架包（[dotnet/runtime#51574](https://github.com/dotnet/runtime/issues/51574) 至今未解决），也无法通过手动添加 `<PackageDependency Name="Microsoft.NETCore.App">` 绕过——实测会以 `0x80073CF3`“依赖于找不到的框架”导致**安装失败**。因此在未安装 .NET 的机器上，启动时会弹框提示用户手动下载 .NET 11 桌面运行时。

若希望消除该提示，把 `SelfContained` 改为 `true`（`VolumeLock.csproj`、两个 `Properties/PublishProfiles/win-*.pubxml`、`Build-StoreBundle.ps1` 需同步修改）：运行时会被打进 MSIX，捆绑包增大到约 118 MB（x64 约 61 MB、ARM64 约 58 MB），商店按架构分发，单个用户实际下载约 60 MB。

Windows App SDK 保持框架依赖（未设置 `WindowsAppSDKSelfContained`），由商店自动安装并随服务更新。

## 使用说明

1. 从 [Microsoft Store](https://apps.microsoft.com/detail/9P0GM98HNNQD) 安装并启动 VolumeLock。
2. 在“扬声器”或“麦克风”区域选择要控制的设备。
3. 调整音量滑块到目标值。
4. 打开“锁定此设备音量”开关。
5. 保持应用运行。关闭窗口后应用会隐藏到托盘，锁定继续生效。
6. 需要彻底退出时，右键点击托盘图标并选择“退出”。

锁定时仍然可以拖动滑块调整目标音量，锁定值会同步更新。

## 实现原理

VolumeLock 直接调用 Windows Core Audio API：

- 使用 `IMMDeviceEnumerator` 枚举音频终结点
- 使用 `IAudioEndpointVolume` 读取和设置系统主音量
- 通过 `IAudioEndpointVolumeCallback` 监听音量变化
- 外部程序修改音量后立即写回目标值
- 使用每秒执行一次的定时校验作为兜底

托盘功能基于 Win32 `Shell_NotifyIcon`、弹出菜单和隐藏消息窗口实现，不依赖第三方托盘库。

## 项目结构

```text
VolumeLock/
├─ Program.cs                 # 应用入口、单实例与激活重定向
├─ App.xaml / App.xaml.cs     # 应用生命周期、托盘接入、退出清理
├─ MainWindow.xaml(.cs)       # 主窗口、标题栏与主题
├─ MainPage.xaml(.cs)         # 音量设置、锁定状态与交互逻辑
├─ AudioVolumeService.cs      # Core Audio COM 互操作、音量读取/设置/锁定
├─ TrayIconService.cs         # 托盘图标、右键菜单与图标状态切换
├─ ThemeService.cs            # 跟随系统浅色/深色主题
├─ LocalizationService.cs     # 资源字符串读取
├─ Strings/                   # en-US、zh-CN 本地化资源
├─ Assets/                    # 应用图标、托盘图标和商店素材
├─ Tools/Generate-Assets.ps1  # 从品牌母版派生全部图标资源
├─ Package.appxmanifest       # MSIX 应用清单
└─ Build-StoreBundle.ps1      # 生成 x64 + ARM64 商店捆绑包
```

## 已知限制

- 锁定的是所选音频设备的系统主音量，不控制单个应用的独立音量。
- 锁定仅在 VolumeLock 运行期间有效，退出应用后会自动解除。
- 部分虚拟音频设备可能不提供标准音量控制接口。
- 本工具通过系统音量回调和定时校验实现恢复，不是驱动级强制保护。

## 隐私

VolumeLock 不需要账号，不包含广告、分析、遥测或追踪，不读取、录制、保存或上传任何音频内容，也不会向服务器发送数据。

详细说明见 [PRIVACY.md](PRIVACY.md)。

## 许可证

本项目使用 [MIT License](LICENSE)。
