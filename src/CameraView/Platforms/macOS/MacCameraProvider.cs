#if MACOS
using AVFoundation;
using CoreMedia;
using CoreVideo;
using System.Runtime.Versioning;
using CameraView.Models;
using CameraView.Services;
using CameraView.Utils;
using SkiaSharp;

namespace CameraView.Platforms.macOS;

/// <summary>
/// macOS 相机提供者 — 基于 AVFoundation（与 iOS 共享相同框架）
/// 无 UIKit 依赖，无陀螺仪/朝向传感器
/// 不支持的功能静默忽略
/// </summary>
internal class MacCameraProvider : ICameraProvider, ICameraPermissions
{
    private readonly AsyncLock updateCameraLock = new();
    private List<PhotoResolution> supportedPhotoResolutions = [.. PhotoResolution.DefaultPresets];

    private AVCaptureSession? session;
    private AVCaptureDevice? captureDevice;
    private AVCaptureDeviceInput? captureInput;
    private AVCaptureVideoDataOutput? videoDataOutput;
    private AVCapturePhotoOutput? photoOutput;
    private FrameAnalyzer? frameAnalyzer;
    private NSObject? zoomObserver;

    private bool started;
    private bool disposed;
    private CameraFacing currentFacing = CameraFacing.Back;
    private List<string>? deviceUniqueIds;
    private int currentDeviceIndex;

    public bool IsInitialized => this.session != null;
    public CameraFacing CurrentFacing => this.currentFacing;
    public float? MinZoomFactor { get; private set; }
    public float? MaxZoomFactor { get; private set; }
    public float? CurrentZoomFactor { get; private set; }
    public FlashMode FlashMode { get; private set; } = FlashMode.Auto;
    public PhotoResolution PhotoResolution { get; private set; } = PhotoResolution.DefaultPresets[0];
    public IReadOnlyList<PhotoResolution> SupportedPhotoResolutions => this.supportedPhotoResolutions;
    public float MinExposureCompensation { get; private set; }
    public float MaxExposureCompensation { get; private set; }
    public float ExposureCompensation { get; private set; }

    public event EventHandler<byte[]>? PhotoCaptured;
    public event EventHandler<SKBitmap>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;

    // ========================================================================
    //  权限
    // ========================================================================

    public Task<bool> CheckPermissionAsync()
    {
        var status = AVCaptureDevice.GetAuthorizationStatus(AVMediaTypes.Video);
        return Task.FromResult(status == AVAuthorizationStatus.Authorized);
    }

    public async Task<bool> RequestPermissionAsync()
    {
        return await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVMediaTypes.Video);
    }

    // ========================================================================
    //  初始化
    // ========================================================================

    public Task InitializeAsync(CameraOptions? options = null)
    {
        if (options?.CameraFacing == CameraFacing.Front)
            this.currentFacing = CameraFacing.Front;

        // 发现所有可用摄像头
        this.deviceUniqueIds = AVCaptureDeviceDiscoverySession.Create(
            [AVCaptureDeviceType.BuiltInWideAngleCamera],
            AVMediaTypes.Video,
            AVCaptureDevicePosition.Unspecified).Devices
            .Select(d => d.UniqueID)
            .ToList();

        this.currentDeviceIndex = 0;

        this.session = new AVCaptureSession();
        this.videoDataOutput = new AVCaptureVideoDataOutput
        {
            AlwaysDiscardsLateVideoFrames = true
        };
        // 请求 BGRA 输出 → FrameAnalyzer 直接内存拷贝
        this.videoDataOutput.WeakVideoSettings = new NSDictionary(
            CVPixelBuffer.PixelFormatTypeKey,
            NSNumber.FromUInt32((uint)CVPixelFormatType.CV32BGRA));

        this.photoOutput = new AVCapturePhotoOutput();

        return Task.CompletedTask;
    }

    // ========================================================================
    //  预览
    // ========================================================================

    public async Task StartPreviewAsync()
    {
        if (this.session == null)
            throw new InvalidOperationException("Camera not initialized.");

        using (await this.updateCameraLock.LockAsync())
        {
            try
            {
                if (this.started)
                {
                    this.session.StopRunning();
                    this.started = false;
                }

                await UpdateCameraAsync();
                UpdateOutput();
                UpdateDeviceCapabilities();

                this.session.StartRunning();
                this.started = true;
            }
            catch (Exception ex)
            {
                this.ErrorOccurred?.Invoke(this, $"Start preview failed: {ex.Message}");
            }
        }
    }

    public Task StopPreviewAsync()
    {
        if (this.session?.Running == true)
        {
            this.session.StopRunning();
            this.started = false;
        }
        return Task.CompletedTask;
    }

    // ========================================================================
    //  拍照
    // ========================================================================

    public Task TakePhotoAsync()
    {
        if (this.photoOutput == null)
            throw new InvalidOperationException("Camera preview not started.");

        var settings = AVCapturePhotoSettings.Create();
        settings.FlashMode = this.FlashMode switch
        {
            FlashMode.Off => AVCaptureFlashMode.Off,
            FlashMode.On => AVCaptureFlashMode.On,
            FlashMode.Auto => AVCaptureFlashMode.Auto,
            _ => AVCaptureFlashMode.Auto
        };
        if (OperatingSystem.IsMacOSVersionAtLeast(13))
            settings.MaxPhotoDimensions = new CMVideoDimensions(this.PhotoResolution.Width, this.PhotoResolution.Height);
        var delegateHandler = new PhotoCaptureDelegate(
            data => this.PhotoCaptured?.Invoke(this, data.ToArray()),
            error => this.ErrorOccurred?.Invoke(this, error));

        this.photoOutput.CapturePhoto(settings, delegateHandler);

        return Task.CompletedTask;
    }

    // ========================================================================
    //  切换摄像头（macOS 上遍历外接/内置设备）
    // ========================================================================

    public async Task SwitchCameraAsync(CameraFacing facing)
    {
        // 桌面端多摄像头：循环切换（忽略 facing）
        if (this.deviceUniqueIds == null || this.deviceUniqueIds.Count <= 1)
            return;

        var wasRunning = this.started;
        await StopPreviewAsync();

        this.currentDeviceIndex = (this.currentDeviceIndex + 1) % this.deviceUniqueIds.Count;

        using (await this.updateCameraLock.LockAsync())
        {
            await UpdateCameraAsync();
            UpdateOutput();
            UpdateDeviceCapabilities();

            if (wasRunning)
            {
                this.session?.StartRunning();
                this.started = true;
            }
        }
    }

    // ========================================================================
    //  不支持的功能 — 静默忽略
    // ========================================================================

    public Task SetFocusAsync(float normalizedX, float normalizedY)
        => Task.CompletedTask;

    public Task SetTorchAsync(bool on)
        => Task.CompletedTask;

    public Task SetZoomAsync(float zoomFactor)
        => Task.CompletedTask;

    public Task SetFlashModeAsync(FlashMode mode)
    {
        this.FlashMode = mode;
        return Task.CompletedTask;
    }

    public Task SetPhotoResolutionAsync(PhotoResolution resolution)
    {
        this.PhotoResolution = this.GetBestSupportedPhotoResolution(resolution);

        if (this.session?.Running == true)
        {
            return this.RestartPreviewAsync();
        }

        return Task.CompletedTask;
    }

    public Task SetExposureCompensationAsync(float ev)
    {
        this.ExposureCompensation = ev;
        return Task.CompletedTask;
    }

    // ========================================================================
    //  会话管理（参考 iOS，移除 UIKit 依赖）
    // ========================================================================

    private Task UpdateCameraAsync()
    {
        if (this.session == null || this.videoDataOutput == null || this.photoOutput == null)
            return Task.CompletedTask;

        this.session.BeginConfiguration();
        try
        {
            if (this.captureInput != null && this.session.Inputs.Contains(this.captureInput))
            {
                this.session.RemoveInput(this.captureInput);
                this.captureInput.Dispose();
            }

            // 从设备列表按当前索引选择
            var uid = this.deviceUniqueIds != null && this.currentDeviceIndex < this.deviceUniqueIds.Count
                ? this.deviceUniqueIds[this.currentDeviceIndex]
                : null;

            var allDevices = AVCaptureDeviceDiscoverySession.Create(
                [AVCaptureDeviceType.BuiltInWideAngleCamera],
                AVMediaTypes.Video,
                AVCaptureDevicePosition.Unspecified).Devices;

            this.captureDevice = allDevices.FirstOrDefault(d => d.UniqueID == uid)
                ?? allDevices.FirstOrDefault();

            if (this.captureDevice == null)
                throw new Exception("未找到摄像头设备。");

            // 更新当前 facing（外接摄像头可能无法获取 panel）
            if (this.captureDevice.Position == AVCaptureDevicePosition.Front)
                this.currentFacing = CameraFacing.Front;
            else if (this.captureDevice.Position == AVCaptureDevicePosition.Back)
                this.currentFacing = CameraFacing.Back;

            this.captureInput = new AVCaptureDeviceInput(this.captureDevice, out var error);
            if (error != null)
                throw new Exception($"创建输入失败: {error}");

            if (this.session.CanAddInput(this.captureInput))
                this.session.AddInput(this.captureInput);

            if (!this.session.Outputs.Contains(this.videoDataOutput) &&
                this.session.CanAddOutput(this.videoDataOutput))
            {
                this.session.AddOutput(this.videoDataOutput);
            }

            if (!this.session.Outputs.Contains(this.photoOutput) &&
                this.session.CanAddOutput(this.photoOutput))
            {
                this.session.AddOutput(this.photoOutput);
            }

            this.UpdateSupportedPhotoResolutions();
            this.PhotoResolution = this.GetBestSupportedPhotoResolution(this.PhotoResolution);
            this.session.SessionPreset = MapResolutionToPreset(this.PhotoResolution);
            if (OperatingSystem.IsMacOSVersionAtLeast(13))
                this.photoOutput.MaxPhotoDimensions = new CMVideoDimensions(this.PhotoResolution.Width, this.PhotoResolution.Height);
        }
        finally
        {
            this.session.CommitConfiguration();
        }

        return Task.CompletedTask;
    }

    private void UpdateOutput()
    {
        if (this.videoDataOutput == null) return;

        this.videoDataOutput.SetSampleBufferDelegate(null, null);
        this.frameAnalyzer?.Dispose();
        this.frameAnalyzer = null;

        this.frameAnalyzer = new FrameAnalyzer(frame =>
        {
            this.FrameReceived?.Invoke(this, frame);
        });

        this.videoDataOutput.SetSampleBufferDelegate(
            this.frameAnalyzer,
            CoreFoundation.DispatchQueue.DefaultGlobalQueue);
    }

    private void UpdateDeviceCapabilities()
    {
        if (this.captureDevice == null) return;

        this.MinZoomFactor = MathF.Round((float)this.captureDevice.MinAvailableVideoZoomFactor, 1);
        this.MaxZoomFactor = MathF.Round((float)this.captureDevice.MaxAvailableVideoZoomFactor, 1);
        this.CurrentZoomFactor = MathF.Round((float)this.captureDevice.VideoZoomFactor, 1);
        this.MinExposureCompensation = this.captureDevice.MinExposureTargetBias;
        this.MaxExposureCompensation = this.captureDevice.MaxExposureTargetBias;
        this.ExposureCompensation = this.captureDevice.ExposureTargetBias;

        // KVO 缩放变化
        this.zoomObserver?.Dispose();
        this.zoomObserver = this.captureDevice.AddObserver(
            "videoZoomFactor",
            NSKeyValueObservingOptions.New,
            _ =>
            {
                if (this.captureDevice != null)
                    this.CurrentZoomFactor = MathF.Round((float)this.captureDevice.VideoZoomFactor, 1);
            });
    }

    private void UpdateSupportedPhotoResolutions()
    {
        if (this.captureDevice == null)
        {
            this.supportedPhotoResolutions = [.. PhotoResolution.DefaultPresets];
            return;
        }

        List<PhotoResolution> resolutions = [];

        if (OperatingSystem.IsMacOSVersionAtLeast(13))
            resolutions = this.EnumerateSupportedPhotoResolutionsMacOS13();

        this.supportedPhotoResolutions = resolutions.Count > 0
            ? resolutions
            : [.. PhotoResolution.DefaultPresets];
    }

    private PhotoResolution GetBestSupportedPhotoResolution(PhotoResolution preferred)
    {
        if (this.supportedPhotoResolutions.Count == 0)
            return preferred;

        var exact = this.supportedPhotoResolutions.FirstOrDefault(r =>
            r.Width == preferred.Width && r.Height == preferred.Height);
        if (exact != null)
            return exact;

        var preferredPixels = preferred.Width * preferred.Height;
        return this.supportedPhotoResolutions
            .OrderBy(resolution => Math.Abs((resolution.Width * resolution.Height) - preferredPixels))
            .ThenBy(resolution => Math.Abs(((double)resolution.Width / resolution.Height) - preferred.AspectRatio))
            .First();
    }

    [SupportedOSPlatform("macos13.0")]
    private List<PhotoResolution> EnumerateSupportedPhotoResolutionsMacOS13()
    {
        if (this.captureDevice == null)
            return [];

        return this.captureDevice.Formats
            .SelectMany(format => format.SupportedMaxPhotoDimensions ?? [])
            .Where(dim => dim.Width > 0 && dim.Height > 0)
            .Select(dim => new PhotoResolution(dim.Width, dim.Height, CreatePhotoResolutionLabel(dim.Width, dim.Height)))
            .DistinctBy(resolution => (resolution.Width, resolution.Height))
            .OrderByDescending(resolution => resolution.Width * resolution.Height)
            .ThenByDescending(resolution => resolution.Width)
            .ToList();
    }

    private static string CreatePhotoResolutionLabel(int width, int height)
    {
        var ratio = (double)width / height;
        var megapixels = width * height / 1_000_000d;
        var ratioLabel = Math.Abs(ratio - 4d / 3d) < 0.03 ? "4:3"
            : Math.Abs(ratio - 16d / 9d) < 0.03 ? "16:9"
            : Math.Abs(ratio - 1d) < 0.03 ? "1:1"
            : $"{ratio:F2}:1";

        return $"{ratioLabel} {megapixels:F1}MP";
    }

    private async Task RestartPreviewAsync()
    {
        await StopPreviewAsync();
        await StartPreviewAsync();
    }

    private static AVCaptureSession.Preset MapResolutionToPreset(PhotoResolution resolution)
    {
        return (resolution.Width, resolution.Height) switch
        {
            (3840, 2160) => AVCaptureSession.Preset3840x2160,
            (1920, 1080) => AVCaptureSession.Preset1920x1080,
            (1280, 720) => AVCaptureSession.Preset1280x720,
            (640, 480) => AVCaptureSession.Preset640x480,
            (352, 288) => AVCaptureSession.Preset352x288,
            (960, 540) => AVCaptureSession.PresetiFrame960x540,
            _ when resolution.Width * resolution.Height > 1920 * 1080 => AVCaptureSession.PresetHigh,
            _ => AVCaptureSession.Preset1280x720,
        };
    }

    // ========================================================================
    //  帧分析器（AVFoundation，BGRA 直接内存拷贝）
    // ========================================================================

    private class FrameAnalyzer : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        private readonly Action<SKBitmap> onFrameReceived;
        private bool disposed;

        public FrameAnalyzer(Action<SKBitmap> onFrameReceived)
        {
            this.onFrameReceived = onFrameReceived;
        }

        public override void DidOutputSampleBuffer(
            AVCaptureOutput captureOutput,
            CMSampleBuffer sampleBuffer,
            AVCaptureConnection connection)
        {
            if (this.disposed) return;

            using var imageBuffer = sampleBuffer.GetImageBuffer();
            if (imageBuffer is not CVPixelBuffer pixelBuffer) return;

            pixelBuffer.Lock(CVPixelBufferLock.ReadOnly);
            try
            {
                var pixelFormat = pixelBuffer.PixelFormatType;

                if (pixelFormat == CVPixelFormatType.CV32BGRA)
                    CopyBGRA(pixelBuffer);
                else
                    CopyFallback(pixelBuffer);
            }
            finally
            {
                pixelBuffer.Unlock(CVPixelBufferLock.ReadOnly);
            }
        }

        private unsafe void CopyBGRA(CVPixelBuffer pixelBuffer)
        {
            int width = (int)pixelBuffer.Width;
            int height = (int)pixelBuffer.Height;
            int bytesPerRow = (int)pixelBuffer.BytesPerRow;
            var baseAddress = pixelBuffer.BaseAddress;

            if (baseAddress == IntPtr.Zero) return;

            var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            Buffer.MemoryCopy(
                baseAddress.ToPointer(),
                skBitmap.GetPixels().ToPointer(),
                height * bytesPerRow,
                height * bytesPerRow);

            this.onFrameReceived(skBitmap);
        }

        private void CopyFallback(CVPixelBuffer pixelBuffer)
        {
            using var ciImage = new CoreImage.CIImage(pixelBuffer);
            using var context = new CoreImage.CIContext();
            using var cgImage = context.CreateCGImage(ciImage, ciImage.Extent);
            if (cgImage == null) return;

            int width = (int)cgImage.Width;
            int height = (int)cgImage.Height;
            int bpr = (int)cgImage.BytesPerRow;
            var data = new byte[height * bpr];

            using var cs = CoreGraphics.CGColorSpace.CreateDeviceRGB();
            using var ctx = new CoreGraphics.CGBitmapContext(
                data, width, height, 8, bpr,
                cs, CoreGraphics.CGBitmapFlags.PremultipliedFirst | CoreGraphics.CGBitmapFlags.ByteOrder32Little);
            ctx.DrawImage(new CoreGraphics.CGRect(0, 0, width, height), cgImage);

            var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            unsafe
            {
                fixed (byte* p = data)
                {
                    Buffer.MemoryCopy(p, skBitmap.GetPixels().ToPointer(), data.Length, data.Length);
                }
            }

            this.onFrameReceived(skBitmap);
        }

        protected override void Dispose(bool disposing)
        {
            this.disposed = true;
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    //  拍照回调委托
    // ========================================================================

    private class PhotoCaptureDelegate : AVCapturePhotoCaptureDelegate
    {
        private readonly Action<NSData> onComplete;
        private readonly Action<string> onError;

        public PhotoCaptureDelegate(Action<NSData> onComplete, Action<string> onError)
        {
            this.onComplete = onComplete;
            this.onError = onError;
        }

        public override void DidFinishProcessingPhoto(
            AVCapturePhotoOutput output,
            AVCapturePhoto photo,
            NSError? error)
        {
            if (error != null)
            {
                this.onError(error.LocalizedDescription);
                return;
            }

            var data = photo.FileDataRepresentation;
            if (data != null)
                this.onComplete(data);
        }
    }

    // ========================================================================
    //  清理
    // ========================================================================

    public void Dispose()
    {
        this.disposed = true;
        this.zoomObserver?.Dispose();
        StopPreviewAsync();
        this.videoDataOutput?.Dispose();
        this.photoOutput?.Dispose();
        this.captureInput?.Dispose();
        this.captureDevice?.Dispose();
        this.session?.Dispose();
    }
}
#endif
