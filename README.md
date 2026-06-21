# CameraView.Avalonia

跨平台相机预览与拍照控件，基于 Avalonia UI 框架。

## 支持的平台

| 平台 | TFM |
|------|-----|
| Android | `net10.0-android` |
| iOS | `net10.0-ios` |
| Windows | `net10.0-windows10.0.19041.0` |
| macOS | `net10.0-macos` |
| Browser (WASM) | `net10.0-browser` |
| Linux | `net10.0` (需 Linux 主机编译) |

## 安装

```shell
dotnet add package CameraView.Avalonia
```

## 快速开始

### 1. XAML 引用

```xml
xmlns:cv="using:CameraView"

<cv:CameraViewControl x:Name="CameraControl" />
```

### 2. 启动相机

```csharp
// 方式一：绑定 CameraEnabled（推荐，自动初始化）
CameraEnabled = true;

// 方式二：手动初始化并启动（需要 provider 引用时用此方式）
var provider = CameraProviderFactory.Create();
await CameraControl.InitializeCameraAsync(provider);
CameraEnabled = true;  // 自动触发 StartCameraAsync
```

> 💡 `CameraEnabled = true` 会自动判断是否需要初始化，无需手动调用 `InitializeCameraAsync`。

### 3. Windows 项目配置

桌面项目的 `.csproj` 中 `TargetFramework` 必须改为 Windows 专用 TFM：

```diff
<!--  ❌ 错误（不含 Windows API，CameraView 无法加载） -->
- <TargetFramework>net10.0</TargetFramework>

<!--  ✅ 正确 -->
+ <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
```

> ⚠️ 多项目结构中，**引用 CameraView 的项目**（如 `MyApp.Desktop.csproj`）需要改，
> 不是共享库项目（`MyApp.csproj` 保持 `net10.0`）。

### 4. App.axaml — 加载控件模板

```xml
<Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://CameraView/CameraViewControl.axaml" />
</Application.Styles>
```

### 5. Android 配置

> ⚠️ Android EXE 项目（如 `MyApp.Android.csproj`）**必须**直接引用 `CameraView.Avalonia`，
> 仅靠共享库（`net10.0`）间接引用不会触发 `buildTransitive .targets` 注入 Android JNI 文件。

```xml
<!-- MyApp.Android.csproj -->
<ItemGroup>
  <PackageReference Include="CameraView.Avalonia" />
  <ProjectReference Include="..\MyApp\MyApp.csproj" />
</ItemGroup>
```

```csharp
// MainActivity.OnCreate — 必须在 base.OnCreate 之前注册
var context = this.BaseContext;
var provider = new CameraView.Platforms.Android.AndroidCameraProvider(context);
CameraView.CameraProviderFactory.RegisterProvider(provider);

base.OnCreate(savedInstanceState);

CameraView.CameraProviderFactory.SetAndroidActivity(this);
```

### 6. 权限

```xml
<!-- AndroidManifest.xml -->
<uses-permission android:name="android.permission.CAMERA" />
```

```csharp
// 启动前手动申请权限
var perms = CameraProviderFactory.CreatePermissions(provider);
if (!await perms.CheckPermissionAsync())
    if (!await perms.RequestPermissionAsync())
        return; // 无权限，ErrorOccurred 会触发说明

CameraEnabled = true;
```

无权限时相机启动会失败，通过 `ErrorOccurred` 事件输出错误信息。

## 功能

- [x] 相机预览（实时帧）
- [x] 拍照（JPEG 输出）
- [x] 前后摄像头切换
- [x] 点击对焦
- [x] 捏合缩放
- [x] 手电筒（闪光灯）
- [x] 闪光灯模式（自动/开/关）
- [x] 曝光补偿
- [x] 拍照分辨率选择（自动查询支持的分辨率）
- [x] 设备朝向传感器（重力方向）
- [x] 照片 EXIF 方向校正（iOS/Android 拍照时自动旋转像素）
- [x] 调试模式（显示 FPS）
