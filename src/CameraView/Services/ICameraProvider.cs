using CameraView.Models;
using SkiaSharp;

namespace CameraView.Services;

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

    event EventHandler<byte[]>? PhotoCaptured;
    event EventHandler<SKBitmap>? FrameReceived;
    event EventHandler<string>? ErrorOccurred;

    Task InitializeAsync(CameraOptions? options = null);
    Task StartPreviewAsync();
    Task StopPreviewAsync();
    Task TakePhotoAsync();
    Task SwitchCameraAsync(CameraFacing facing);
    Task SetFocusAsync(float normalizedX, float normalizedY);
    Task SetZoomAsync(float zoomFactor);
    Task SetTorchAsync(bool on);
    Task SetFlashModeAsync(FlashMode mode);
    Task SetPhotoResolutionAsync(PhotoResolution resolution);
}