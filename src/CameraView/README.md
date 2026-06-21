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

无权限时相机启动会失败，通过 `ErrorCommand`（MVVM）或 `CameraError` 事件输出错误信息。

```xml
<!-- XAML — MVVM 绑定 -->
<cv:CameraViewControl ErrorCommand="{Binding OnCameraErrorCommand}" />
```

```csharp
// ViewModel
[RelayCommand]
private void OnCameraError(string error)
{
    StatusText = $"相机错误: {error}";
}
```

或者订阅事件：
```csharp
CameraControl.CameraError += (_, error) => Debug.WriteLine(error);
```


> 💡 **调试模式性能提示：** Debug 模式下 Mono 解释器/未优化 JIT 会导致 YUV→BGRA 帧转换性能显著降低，预览帧率可能降至 2-8fps。
> 使用 `Release` 配置（AOT 编译）可获得 25-30fps 流畅预览。
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

---

## API 参考

### 依赖属性（可绑定）

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `CameraEnabled` | `bool` | `false` | 开关相机（TwoWay） |
| `CameraFacing` | `CameraFacing` | `Back` | 前后摄像头（TwoWay） |
| `IsFrontCamera` | `bool` | `false` | 是否为前置摄像头（TwoWay） |
| `TorchOn` | `bool` | `false` | 手电筒（TwoWay） |
| `RequestZoomFactor` | `float?` | `null` | 请求缩放倍率（TwoWay） |
| `CurrentZoomFactor` | `float?` | `null` | 当前缩放倍率（只读） |
| `TapToFocusEnabled` | `bool` | `true` | 点击对焦开关（TwoWay） |
| `PinchToZoomEnabled` | `bool` | `true` | 捏合缩放开关（TwoWay） |
| `FlashMode` | `FlashMode` | `Auto` | 闪光灯模式（TwoWay） |
| `PhotoResolution` | `PhotoResolution?` | `null` | 照片分辨率（TwoWay） |
| `SupportedResolutions` | `IReadOnlyList<PhotoResolution>` | — | 支持的照片分辨率列表（只读） |
| `ExposureCompensation` | `float` | `0` | 曝光补偿 EV（TwoWay） |
| `DebugMode` | `bool` | `false` | 调试模式：显示 FPS 叠加层（TwoWay） |
| `IsCapturingNextFrame` | `bool` | `false` | 触发拍照：设为 true 触发拍照，完成后重置（TwoWay） |
| `IsBusying` | `bool` | `false` | 忙碌状态：拍照/切换时为 true（OneWayToSource） |
| `DeviceOrientation` | `DeviceOrientation?` | `null` | 设备朝向原始数据（OneWayToSource） |
| `OrientationState` | `DeviceOrientationState` | `PortraitUpright` | 设备朝向状态（OneWayToSource） |
| `PreviewAspectRatio` | `double` | `4/3` | 预览帧宽高比（只读） |
| `FocusIndicatorStroke` | `IBrush` | `DeepPink` | 对焦圆环颜色（TwoWay） |
| `FocusIndicatorStrokeThickness` | `double` | `2.0` | 对焦圆环粗细（TwoWay） |
| `CameraProvider` | `ICameraProvider?` | `null` | 外部注入的相机提供者 |
| `PhotoCapturedCommand` | `ICommand?` | `null` | 拍照完成命令（参数 `PhotoCaptureResult`） |
| `ErrorCommand` | `ICommand?` | `null` | 相机错误命令（参数 `string`） |

### 方法

| 方法 | 返回 | 说明 |
|------|------|------|
| `InitializeCameraAsync(ICameraProvider)` | `Task` | 初始化相机：注册事件 + 平台初始化 |
| `StartCameraAsync()` | `Task` | 启动预览（如果未初始化则自动初始化） |
| `StopCameraAsync()` | `Task` | 停止预览 |
| `TakePhotoAsync()` | `Task` | 拍照 |

### 事件

| 事件 | 参数 | 说明 |
|------|------|------|
| `PhotoCaptured` | `EventHandler<byte[]>` | 拍照完成（JPEG 原始字节） |
| `CameraError` | `EventHandler<string>` | 相机错误 |
| `DeviceOrientationChanged` | `EventHandler<DeviceOrientation>` | 设备朝向变化（Pitch/Roll/Yaw + 重力向量） |

---

## 平台支持

| 功能 | Android | iOS | Windows | macOS | Browser | Linux |
|------|---------|-----|---------|-------|---------|-------|
| 相机预览 | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ |
| 拍照（JPEG） | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ |
| 前后摄像头切换 | ✅ | ✅ | ✅ | — | ✅ | — |
| 点击对焦 | ✅ | ✅ | — | — | — | — |
| 捏合缩放 | ✅ | ✅ | — | — | — | — |
| 手电筒 / 闪光灯 | ✅ | ✅ | — | — | — | — |
| 闪光灯模式 | ✅ | ✅ | ✅ | — | — | — |
| 曝光补偿 | ✅ | ✅ | — | — | — | — |
| 拍照分辨率选择 | ✅ | ✅ | ✅ | — | — | — |
| 设备朝向传感器 | ✅ | ✅ | — | — | — | — |
| 照片 EXIF 方向校正 | ✅ | ✅ | — | — | — | — |
| 调试模式（FPS） | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ |
