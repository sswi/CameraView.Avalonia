#if BROWSER
using System.Runtime.InteropServices.JavaScript;
using CameraView.Models;
using CameraView.Services;
using SkiaSharp;

namespace CameraView.Platforms.Browser;

/// <summary>
/// 浏览器 (WASM) 相机提供者 — 基于 WebRTC getUserMedia + Canvas 2D
/// 需配合 cameraView.js ES module 使用
/// 不支持的功能静默忽略
/// </summary>
internal partial class BrowserCameraProvider : ICameraProvider
{
    // ========================================================================
    //  JS 互操作 — 导入 cameraView.js
    // ========================================================================

    [JSImport("startCamera", "cameraView.js")]
    private static partial Task<bool> StartCameraJS(string facingMode, int width, int height);

    [JSImport("stopCamera", "cameraView.js")]
    private static partial void StopCameraJS();

    [JSImport("getFrameData", "cameraView.js")]
    private static partial byte[]? GetFrameDataJS();

    [JSImport("capturePhoto", "cameraView.js")]
    private static partial string CapturePhotoJS();

    // ========================================================================
    //  成员变量
    // ========================================================================

    private readonly int frameWidth = 640;
    private readonly int frameHeight = 480;
    private CancellationTokenSource? cts;
    private bool started;

    public bool IsInitialized => true;
    public CameraFacing CurrentFacing { get; private set; } = CameraFacing.Back;
    public float? MinZoomFactor { get; private set; }
    public float? MaxZoomFactor { get; private set; }
    public float? CurrentZoomFactor { get; private set; }
    public FlashMode FlashMode { get; private set; } = FlashMode.Auto;
    public PhotoResolution PhotoResolution { get; private set; } = PhotoResolution.DefaultPresets[0];
    public IReadOnlyList<PhotoResolution> SupportedPhotoResolutions => PhotoResolution.DefaultPresets;
    public float MinExposureCompensation { get; private set; }
    public float MaxExposureCompensation { get; private set; }
    public float ExposureCompensation { get; private set; }

    public event EventHandler<byte[]>? PhotoCaptured;
    public event EventHandler<SKBitmap>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;

    // ========================================================================
    //  初始化
    // ========================================================================

    public Task InitializeAsync(CameraOptions? options = null)
    {
        if (options?.CameraFacing == CameraFacing.Front)
            CurrentFacing = CameraFacing.Front;
        return Task.CompletedTask;
    }

    // ========================================================================
    //  预览（Canvas RGBA → SKBitmap Rgba8888，无需字节交换）
    // ========================================================================

    public async Task StartPreviewAsync()
    {
        try
        {
            var facing = CurrentFacing == CameraFacing.Front ? "user" : "environment";

            bool ok = await StartCameraJS(facing, this.frameWidth, this.frameHeight);
            if (!ok)
            {
                // 回退：尝试反方向或无约束
                ok = await StartCameraJS("", this.frameWidth, this.frameHeight);
                if (!ok)
                {
                    this.ErrorOccurred?.Invoke(this, "浏览器拒绝访问摄像头。");
                    return;
                }
                // 无法确定实际 facing，保持默认
            }

            this.started = true;
            this.cts = new CancellationTokenSource();
            _ = FrameLoopAsync(this.cts.Token);
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"启动预览失败: {ex.Message}");
        }
    }

    private async Task FrameLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33)); // ~30fps
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var data = GetFrameDataJS();
                if (data == null || data.Length == 0) continue;

                // Canvas.getImageData 返回 RGBA → SKBitmap.Rgba8888 直接匹配
                var bmp = new SKBitmap(this.frameWidth, this.frameHeight,
                    SKColorType.Rgba8888, SKAlphaType.Unpremul);

                unsafe
                {
                    fixed (byte* p = data)
                    {
                        Buffer.MemoryCopy(p, bmp.GetPixels().ToPointer(),
                            data.Length, data.Length);
                    }
                }

                this.FrameReceived?.Invoke(this, bmp);
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task StopPreviewAsync()
    {
        this.started = false;
        this.cts?.Cancel();
        this.cts?.Dispose();
        this.cts = null;
        StopCameraJS();
        return Task.CompletedTask;
    }

    // ========================================================================
    //  拍照（Canvas.toBlob → JPEG 字节）
    // ========================================================================

    public Task TakePhotoAsync()
    {
        try
        {
            var dataUrl = CapturePhotoJS();
            if (string.IsNullOrEmpty(dataUrl)) return Task.CompletedTask;

            // data:image/jpeg;base64,/9j/4AAQ...
            var base64 = dataUrl.AsSpan(dataUrl.IndexOf(',') + 1);
            var jpeg = Convert.FromBase64String(base64);
            this.PhotoCaptured?.Invoke(this, jpeg);
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"拍照失败: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    // ========================================================================
    //  切换摄像头
    // ========================================================================

    public async Task SwitchCameraAsync(CameraFacing facing)
    {
        CurrentFacing = facing;
        var wasRunning = this.started;
        await StopPreviewAsync();
        if (wasRunning)
            await StartPreviewAsync();
    }

    // ========================================================================
    //  不支持的功能 — 静默忽略
    // ========================================================================

    public Task SetFocusAsync(float normalizedX, float normalizedY) => Task.CompletedTask;
    public Task SetTorchAsync(bool on) => Task.CompletedTask;
    public Task SetZoomAsync(float zoomFactor) => Task.CompletedTask;

    public Task SetFlashModeAsync(FlashMode mode)
    {
        this.FlashMode = mode;
        return Task.CompletedTask;
    }

    public Task SetPhotoResolutionAsync(PhotoResolution resolution)
    {
        this.PhotoResolution = resolution;
        return Task.CompletedTask;
    }

    public Task SetExposureCompensationAsync(float ev)
    {
        this.ExposureCompensation = ev;
        return Task.CompletedTask;
    }

    // ========================================================================
    //  清理
    // ========================================================================

    public void Dispose()
    {
        StopPreviewAsync();
        StopCameraJS();
    }
}
#endif
