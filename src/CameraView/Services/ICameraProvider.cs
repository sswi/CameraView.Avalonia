namespace CameraView.Services;

/// <summary>
/// 平台相机抽象接口 — 统一的相机操作 API
/// </summary>
public interface ICameraProvider : IDisposable
{
    bool IsInitialized { get; }
    CameraFacing CurrentFacing { get; }
    float? MinZoomFactor { get; }
    float? MaxZoomFactor { get; }
    float? CurrentZoomFactor { get; }
    FlashMode FlashMode { get; }
    PhotoResolution PhotoResolution { get; }
    IReadOnlyList<PhotoResolution> SupportedPhotoResolutions { get; }
    float MinExposureCompensation { get; }
    float MaxExposureCompensation { get; }
    float ExposureCompensation { get; }

    /// <summary>拍照完成事件（JPEG 字节）</summary>
    event EventHandler<byte[]>? PhotoCaptured;
    /// <summary>预览帧就绪事件</summary>
    event EventHandler<SKBitmap>? FrameReceived;
    /// <summary>错误事件</summary>
    event EventHandler<string>? ErrorOccurred;

    /// <summary>初始化相机</summary>
    Task InitializeAsync(CameraOptions? options = null);
    /// <summary>启动预览</summary>
    Task StartPreviewAsync();
    /// <summary>停止预览</summary>
    Task StopPreviewAsync();
    /// <summary>拍照</summary>
    Task TakePhotoAsync();
    /// <summary>切换前后摄像头</summary>
    Task SwitchCameraAsync(CameraFacing facing);
    /// <summary>手动对焦（归一化坐标 0~1）</summary>
    Task SetFocusAsync(float normalizedX, float normalizedY);
    /// <summary>设置缩放倍率</summary>
    Task SetZoomAsync(float zoomFactor);
    /// <summary>开关手电筒</summary>
    Task SetTorchAsync(bool on);
    /// <summary>设置闪光灯模式</summary>
    Task SetFlashModeAsync(FlashMode mode);
    /// <summary>设置照片分辨率（重启预览生效）</summary>
    Task SetPhotoResolutionAsync(PhotoResolution resolution);
    /// <summary>设置曝光补偿（EV）</summary>
    Task SetExposureCompensationAsync(float ev);
}
