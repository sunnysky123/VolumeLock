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

从源码构建本项目还需要：

- .NET 10 SDK
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
- `x86`
- `ARM64`

## 从源码运行

```powershell
dotnet run --project VolumeLock.csproj -p:Platform=x64
```

如果使用 Visual Studio，直接按 `F5` 启动即可。

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

输出目录为 `StorePackages`。

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
