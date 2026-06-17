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

### 2. 创建 Provider 并启动

```csharp
// 创建相机提供者
var provider = CameraProviderFactory.Create();
await CameraControl.InitializeCameraAsync(provider);
await CameraControl.StartCameraAsync();
```

### 3. Android 配置

```csharp
// MainActivity.OnCreate — 必须在 base.OnCreate 之前注册
var provider = new CameraView.Platforms.Android.AndroidCameraProvider(this.BaseContext);
CameraView.CameraProviderFactory.RegisterProvider(provider);

base.OnCreate(savedInstanceState);

CameraView.CameraProviderFactory.SetAndroidActivity(this);
```

### 4. 权限

```xml
<!-- AndroidManifest.xml -->
<uses-permission android:name="android.permission.CAMERA" />
```

```csharp
// 拍照前检查权限
var perms = CameraProviderFactory.CreatePermissions(provider);
if (!await perms.CheckPermissionAsync())
    await perms.RequestPermissionAsync();
```

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
