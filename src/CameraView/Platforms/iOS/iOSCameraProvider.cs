using AVFoundation;
using Foundation;
using CoreGraphics;
using CoreMedia;
using CoreVideo;
using System.Runtime.Versioning;
using CameraView.Models;
using CameraView.Services;
using CameraView.Utils;
using SkiaSharp;
using UIKit;

namespace CameraView.Platforms.iOS;

internal class iOSCameraProvider : ICameraProvider, ICameraPermissions
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
    private PhotoCaptureDelegate? currentPhotoDelegate; // 防止 GC 回收
    private bool torchOn;

    private bool started;
    private bool disposed;
    private bool isCapturingPhoto; // 防止拍照回调重复触发
    private CameraFacing currentFacing = CameraFacing.Back;
    private int _rotationAngle = 90; // 当前帧旋转角度，由 FrameAnalyzer 更新

    public bool IsInitialized => this.session != null;
    public CameraFacing CurrentFacing => this.currentFacing;
    public float? MinZoomFactor { get; private set; }
    public float? MaxZoomFactor { get; private set; }
    public float? CurrentZoomFactor { get; private set; }
    public FlashMode FlashMode { get; private set; } = FlashMode.Auto;
    public PhotoResolution PhotoResolution { get; private set; } = PhotoResolution.DefaultPresets[0]; // 4032x3024
    public IReadOnlyList<PhotoResolution> SupportedPhotoResolutions => this.supportedPhotoResolutions;
    public float MinExposureCompensation { get; private set; }
    public float MaxExposureCompensation { get; private set; }
    public float ExposureCompensation { get; private set; }

    public async Task SetPhotoResolutionAsync(PhotoResolution resolution)
    {
        this.PhotoResolution = resolution;

        // 找到支持目标分辨率的 format 并切过去 + 设 MaxPhotoDimensions
        if (this.captureDevice != null && this.photoOutput != null && OperatingSystem.IsIOSVersionAtLeast(16))
        {
            // 支持目标分辨率的 format 中，选预览长边最接近 900 的（平衡清晰度与帧率）
            var target = this.captureDevice.Formats
                .Select(f => new
                {
                    Format = f,
                    Max = (f.SupportedMaxPhotoDimensions ?? []).FirstOrDefault(d => d.Width == resolution.Width && d.Height == resolution.Height),
                    PreviewDims = ((CMVideoFormatDescription)f.FormatDescription).Dimensions,
                })
                .Where(x => x.Max.Width > 0 && x.PreviewDims.Width > 0)
                .OrderBy(x => Math.Abs(Math.Max(x.PreviewDims.Width, x.PreviewDims.Height) - 900))
                .FirstOrDefault();

            if (target != null && !ReferenceEquals(this.captureDevice.ActiveFormat, target.Format))
            {
                try
                {
                    this.captureDevice.LockForConfiguration(out var e);
                    if (e == null) this.captureDevice.ActiveFormat = target.Format;
                    this.captureDevice.UnlockForConfiguration();
                }
                catch { }
            }

            this.photoOutput.MaxPhotoDimensions = new CMVideoDimensions(resolution.Width, resolution.Height);
        }
    }

    public event EventHandler<byte[]>? PhotoCaptured;
    public event EventHandler<SKBitmap>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;

    // ========== ICameraPermissions ==========

    public Task<bool> CheckPermissionAsync()
    {
        var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        return Task.FromResult(status == AVAuthorizationStatus.Authorized);
    }

    public async Task<bool> RequestPermissionAsync()
    {
        return await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video);
    }

    // ========== ICameraProvider ==========

    public Task InitializeAsync(CameraOptions? options = null)
    {
        if (options?.CameraFacing == CameraFacing.Front)
            this.currentFacing = CameraFacing.Front;

        this.session = new AVCaptureSession();
        this.videoDataOutput = new AVCaptureVideoDataOutput
        {
            AlwaysDiscardsLateVideoFrames = true
        };
        // 请求 BGRA 输出，FrameAnalyzer 可直接内存拷贝，跳过 CIImage/CGImage 转换
        this.videoDataOutput.WeakVideoSettings = new NSDictionary(
            CVPixelBuffer.PixelFormatTypeKey,
            NSNumber.FromUInt32((uint)CVPixelFormatType.CV32BGRA));

        this.photoOutput = new AVCapturePhotoOutput();

        return Task.CompletedTask;
    }

    public async Task StartPreviewAsync()
    {
        if (this.session == null)
            throw new InvalidOperationException("Camera not initialized.");

        if (UIDevice.CurrentDevice.Model.Contains("Simulator"))
        {
            this.ErrorOccurred?.Invoke(this, "Camera not available on iOS simulator.");
            return;
        }

        using (await this.updateCameraLock.LockAsync())
        {
            try
            {
                if (this.started)
                {
                    this.session.StopRunning();
                    this.started = false;
                }

                await this.UpdateCameraAsync();
                this.UpdateOutput();
                this.UpdateDeviceCapabilities();
                this.UpdateSupportedPhotoResolutions();

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

    public async Task TakePhotoAsync()
    {
        if (this.photoOutput == null || this.captureDevice == null)
            throw new InvalidOperationException("Camera preview not started.");

        using (await this.updateCameraLock.LockAsync())
        {
            if (this.photoOutput == null || this.captureDevice == null)
                throw new InvalidOperationException("Camera preview not started.");

            // 防止重入
            if (this.isCapturingPhoto)
                return;
            this.isCapturingPhoto = true;

            // MAUI 风格：format 已在 UpdateCameraAsync 中设好，拍照时不再切换，不卡顿
            if (OperatingSystem.IsIOSVersionAtLeast(16))
            {
            }

            var settings = AVCapturePhotoSettings.Create();
            settings.FlashMode = this.FlashMode switch
            {
                FlashMode.Off => AVCaptureFlashMode.Off,
                FlashMode.On => AVCaptureFlashMode.On,
                FlashMode.Auto => AVCaptureFlashMode.Auto,
                _ => AVCaptureFlashMode.Auto
            };
            if (OperatingSystem.IsIOSVersionAtLeast(16))
            {
                // format 已在 SetPhotoResolutionAsync 中切换，直接指定
                settings.MaxPhotoDimensions = new CMVideoDimensions(
                    this.PhotoResolution.Width, this.PhotoResolution.Height);
            }

            currentPhotoDelegate?.Dispose();
            currentPhotoDelegate = new PhotoCaptureDelegate(
                data =>
                {
                    currentPhotoDelegate = null;
                    this.isCapturingPhoto = false;
                    var rotated = RotatePhotoData(data, this._rotationAngle);
                    UIKit.UIApplication.SharedApplication.InvokeOnMainThread(() =>
                        this.PhotoCaptured?.Invoke(this, rotated));
                },
                error =>
                {
                    currentPhotoDelegate = null;
                    this.isCapturingPhoto = false;
                    this.ErrorOccurred?.Invoke(this, error);
                });

            try
            {
                this.photoOutput.CapturePhoto(settings, currentPhotoDelegate);
            }
            catch (Exception ex)
            {
                this.isCapturingPhoto = false;
                this.ErrorOccurred?.Invoke(this, $"CapturePhoto failed: {ex.Message}");
                currentPhotoDelegate = null;
            }
        }
    }

    public async Task SwitchCameraAsync(CameraFacing facing)
    {
        this.currentFacing = facing;
        using (await this.updateCameraLock.LockAsync())
        {
            try
            {
                // 必须先停止 session，再更新配置，否则 AVFoundation 会抛异常导致闪退
                if (this.started && this.session?.Running == true)
                {
                    this.session.StopRunning();
                    this.started = false;
                }

                await this.UpdateCameraAsync();
                this.UpdateOutput();
                this.UpdateDeviceCapabilities();
                this.UpdateSupportedPhotoResolutions();

                if (this.session != null)
                {
                    this.session.StartRunning();
                    this.started = true;
                }
            }
            catch (Exception ex)
            {
                this.ErrorOccurred?.Invoke(this, $"Switch camera failed: {ex.Message}");
            }
        }
    }

    public Task SetFocusAsync(float normalizedX, float normalizedY)
    {
        if (this.captureDevice == null) return Task.CompletedTask;

        try
        {
            this.captureDevice.LockForConfiguration(out _);
            try
            {
                if (this.captureDevice.FocusPointOfInterestSupported)
                {
                    this.captureDevice.FocusPointOfInterest = new CGPoint(normalizedX, normalizedY);
                    this.captureDevice.FocusMode = AVCaptureFocusMode.AutoFocus;
                }

                if (this.captureDevice.ExposurePointOfInterestSupported)
                {
                    this.captureDevice.ExposurePointOfInterest = new CGPoint(normalizedX, normalizedY);
                    this.captureDevice.ExposureMode = AVCaptureExposureMode.AutoExpose;
                }
            }
            finally
            {
                this.captureDevice.UnlockForConfiguration();
            }
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"Focus failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task SetZoomAsync(float zoomFactor)
    {
        if (this.captureDevice == null) return Task.CompletedTask;

        try
        {
            this.captureDevice.LockForConfiguration(out _);
            try
            {
                var clamped = Math.Clamp(zoomFactor,
                    (float)this.captureDevice.MinAvailableVideoZoomFactor,
                    (float)this.captureDevice.MaxAvailableVideoZoomFactor);

                this.captureDevice.VideoZoomFactor = clamped;
                this.CurrentZoomFactor = clamped;
            }
            finally
            {
                this.captureDevice.UnlockForConfiguration();
            }
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"Zoom failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task SetTorchAsync(bool on)
    {
        if (this.captureDevice == null || !this.captureDevice.HasTorch)
            return Task.CompletedTask;

        try
        {
            this.captureDevice.LockForConfiguration(out _);
            try
            {
                this.torchOn = on;
                this.captureDevice.TorchMode = on
                    ? AVCaptureTorchMode.On
                    : AVCaptureTorchMode.Off;
            }
            finally
            {
                this.captureDevice.UnlockForConfiguration();
            }
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"Torch failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task SetExposureCompensationAsync(float ev)
    {
        if (this.captureDevice == null) return Task.CompletedTask;

        try
        {
            this.captureDevice.LockForConfiguration(out _);
            try
            {
                this.MinExposureCompensation = this.captureDevice.MinExposureTargetBias;
                this.MaxExposureCompensation = this.captureDevice.MaxExposureTargetBias;

                var clamped = Math.Clamp(ev, this.MinExposureCompensation, this.MaxExposureCompensation);
                this.captureDevice.SetExposureTargetBias(clamped, null);
                this.ExposureCompensation = clamped;
            }
            finally
            {
                this.captureDevice.UnlockForConfiguration();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Exposure failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task SetFlashModeAsync(FlashMode mode)
    {
        this.FlashMode = mode;

        // FlashMode.On 时同时开启手电筒（与 Android 行为一致）
        if (this.captureDevice?.HasTorch == true)
        {
            _ = SetTorchAsync(mode == FlashMode.On);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        this.disposed = true;
        this.zoomObserver?.Dispose();
        this.StopPreviewAsync();
        this.videoDataOutput?.Dispose();
        this.photoOutput?.Dispose();
        this.captureInput?.Dispose();
        this.captureDevice?.Dispose();
        this.session?.Dispose();
    }

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

            var position = this.currentFacing == CameraFacing.Back
                ? AVCaptureDevicePosition.Back
                : AVCaptureDevicePosition.Front;

            this.captureDevice = AVCaptureDeviceDiscoverySession.Create(
                [AVCaptureDeviceType.BuiltInWideAngleCamera],
                AVMediaTypes.Video,
                position).Devices.FirstOrDefault();

            if (this.captureDevice == null)
                throw new Exception($"No camera found for {this.currentFacing}");

            this.captureInput = new AVCaptureDeviceInput(this.captureDevice, out var error);
            if (error != null)
                throw new Exception($"Failed to create input: {error}");

            if (this.session.CanAddInput(this.captureInput))
                this.session.AddInput(this.captureInput);

            if (!this.session.Outputs.Contains(this.videoDataOutput) &&
                this.session.CanAddOutput(this.videoDataOutput))
                this.session.AddOutput(this.videoDataOutput);

            if (!this.session.Outputs.Contains(this.photoOutput) &&
                this.session.CanAddOutput(this.photoOutput))
                this.session.AddOutput(this.photoOutput);

            this.session.SessionPreset = GetPreviewSessionPreset(this.PhotoResolution);

            // 设置拍照最大输出尺寸（不切 format，避免预览变糊）
            if (OperatingSystem.IsIOSVersionAtLeast(16) && this.captureDevice != null)
            {
                var max = (this.captureDevice.ActiveFormat.SupportedMaxPhotoDimensions ?? [])
                    .Where(d => d.Width > 0 && d.Height > 0)
                    .OrderByDescending(d => d.Width * d.Height)
                    .FirstOrDefault();

                if (max.Width > 0)
                    this.photoOutput.MaxPhotoDimensions = max;
            }
        }
        finally
        {
            this.session.CommitConfiguration();
        }

        // 注册设备方向变化通知 → 通知 FrameAnalyzer 旋转角度
        UIDevice.Notifications.ObserveOrientationDidChange((_, _) =>
        {
            this.frameAnalyzer?.SetDeviceOrientation(UIDevice.CurrentDevice.Orientation);
        });
        // 初始设置
        this.frameAnalyzer?.SetDeviceOrientation(UIDevice.CurrentDevice.Orientation);

        return Task.CompletedTask;
    }

    private void UpdateOutput()
    {
        if (this.videoDataOutput == null) return;

        this.videoDataOutput.SetSampleBufferDelegate(null, null);
        this.frameAnalyzer?.Dispose();
        this.frameAnalyzer = null;

        this.frameAnalyzer = new FrameAnalyzer(
            frame => this.FrameReceived?.Invoke(this, frame),
            angle => this._rotationAngle = angle);

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

        // KVO 监听缩放倍率变化（与 Android ZoomObserver 行为一致）
        this.zoomObserver?.Dispose();
        this.zoomObserver = (NSObject)this.captureDevice.AddObserver(
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

        if (OperatingSystem.IsIOSVersionAtLeast(16))
            resolutions = this.EnumerateSupportedPhotoResolutionsAcrossAllFormatsiOS16();

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

    [SupportedOSPlatform("ios16.0")]
    private List<PhotoResolution> EnumerateSupportedPhotoResolutionsAcrossAllFormatsiOS16()
    {
        if (this.captureDevice == null)
            return [];

        // MAUI 方式：读 FormatDescription.Dimensions（format 真实像素），跳纯视频 codec
        return this.captureDevice.Formats
            .Select(f => new
            {
                Dims = ((CMVideoFormatDescription)f.FormatDescription).Dimensions,
                Codec = (int)f.FormatDescription.VideoCodecType,
            })
            .Where(x => x.Dims.Width > 0 && x.Dims.Height > 0)
            .Where(x => x.Codec != (int)CVPixelFormatType.CV420YpCbCr8BiPlanarVideoRange)
            .Select(x => new PhotoResolution(
                (int)x.Dims.Width, (int)x.Dims.Height,
                CreatePhotoResolutionLabel((int)x.Dims.Width, (int)x.Dims.Height)))
            .DistinctBy(r => (r.Width, r.Height))
            .OrderByDescending(r => r.Width * r.Height)
            .ThenByDescending(r => r.Width)
            .ToList();
    }

    [SupportedOSPlatform("ios16.0")]
    private (AVCaptureDeviceFormat? Format, PhotoResolution Resolution) GetBestCaptureFormatAndResolutionForPhoto(PhotoResolution preferred)
    {
        if (this.captureDevice == null)
            return (null, preferred);

        var preferredPixels = preferred.Width * preferred.Height;

        // 收集所有 format 中支持目标分辨率的候选
        var allCandidates = this.captureDevice.Formats
            .SelectMany(format => (format.SupportedMaxPhotoDimensions ?? [])
                .Where(dim => dim.Width > 0 && dim.Height > 0)
                .Select(dim => new { Format = format, Resolution = new PhotoResolution(dim.Width, dim.Height, CreatePhotoResolutionLabel(dim.Width, dim.Height)) }))
            .ToList();

        if (allCandidates.Count == 0)
            return (this.captureDevice.ActiveFormat, preferred);

        // 判断分辨率是否与目标足够接近（像素数 ≥ 80%，宽高比偏差 ≤ 0.15）
        bool IsAcceptable(PhotoResolution r)
        {
            var ratio = (double)(r.Width * r.Height) / preferredPixels;
            var aspectDiff = Math.Abs(r.AspectRatio - preferred.AspectRatio);
            return ratio >= 0.8 && aspectDiff <= 0.15;
        }

        // 优先从满足条件的候选中选择，若无则从所有候选中选最接近的
        var acceptable = allCandidates.Where(c => IsAcceptable(c.Resolution)).ToList();
        var candidates = acceptable.Count > 0 ? acceptable : allCandidates;

        var best = candidates
            .OrderBy(c => c.Resolution.Width == preferred.Width && c.Resolution.Height == preferred.Height ? 0 : 1)
            .ThenBy(c => Math.Abs((c.Resolution.Width * c.Resolution.Height) - preferredPixels))
            .ThenBy(c => Math.Abs(c.Resolution.AspectRatio - preferred.AspectRatio))
            .First();

        return (best.Format, best.Resolution);
    }

    private void ApplyCaptureFormat(AVCaptureDeviceFormat format, bool withinSessionConfiguration = false)
    {
        if (this.captureDevice == null || ReferenceEquals(this.captureDevice.ActiveFormat, format))
            return;

        // 在 session.BeginConfiguration/CommitConfiguration 事务内时，
        // session 已持有配置锁，无需再 LockForConfiguration
        if (!withinSessionConfiguration)
            this.captureDevice.LockForConfiguration(out _);
        try
        {
            this.captureDevice.ActiveFormat = format;

            var clampedZoom = Math.Clamp(this.CurrentZoomFactor ?? 1f,
                (float)this.captureDevice.MinAvailableVideoZoomFactor,
                (float)this.captureDevice.MaxAvailableVideoZoomFactor);
            this.captureDevice.VideoZoomFactor = clampedZoom;
            this.CurrentZoomFactor = clampedZoom;

            var clampedExposure = Math.Clamp(this.ExposureCompensation,
                this.captureDevice.MinExposureTargetBias,
                this.captureDevice.MaxExposureTargetBias);
            this.captureDevice.SetExposureTargetBias(clampedExposure, null);
            this.ExposureCompensation = clampedExposure;

            if (this.captureDevice.HasTorch)
            {
                this.captureDevice.TorchMode = this.torchOn ? AVCaptureTorchMode.On : AVCaptureTorchMode.Off;
            }
        }
        finally
        {
            if (!withinSessionConfiguration)
                this.captureDevice.UnlockForConfiguration();
        }
    }

    private async Task RestorePreviewFormatAfterCaptureAsync(AVCaptureDeviceFormat? previewFormat)
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16) || previewFormat == null)
            return;

        try
        {
            using (await this.updateCameraLock.LockAsync())
            {
                if (this.captureDevice == null || ReferenceEquals(this.captureDevice.ActiveFormat, previewFormat))
                    return;

                this.captureDevice.LockForConfiguration(out _);
                try
                {
                    this.captureDevice.ActiveFormat = previewFormat;
                }
                finally
                {
                    this.captureDevice.UnlockForConfiguration();
                }
            }
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"Restore preview format failed: {ex.Message}");
        }
    }

    /// <summary>将 PhotoResolution 映射到 AVCaptureSessionPreset 字符串常量</summary>
    private static NSString GetPreviewSessionPreset(PhotoResolution resolution)
    {
        // 预览会话只使用视频稳定可用的 preset，超高像素仍通过拍照阶段的 format + MaxPhotoDimensions 生效
        return (resolution.Width, resolution.Height) switch
        {
            (3840, 2160) => (NSString)"AVCaptureSessionPreset3840x2160",
            (1920, 1080) => (NSString)"AVCaptureSessionPreset1920x1080",
            (1280, 720)  => (NSString)"AVCaptureSessionPreset1280x720",
            (640, 480)   => (NSString)"AVCaptureSessionPreset640x480",
            (352, 288)   => (NSString)"AVCaptureSessionPreset352x288",
            (960, 540)   => (NSString)"AVCaptureSessionPresetiFrame960x540",
            _ when resolution.Width * resolution.Height > 1920 * 1080 => (NSString)"AVCaptureSessionPresetHigh",
            _ => (NSString)"AVCaptureSessionPreset1280x720",
        };
    }

    [SupportedOSPlatform("ios16.0")]
    private static AVCapturePhotoQualityPrioritization GetPhotoQualityPrioritization(PhotoResolution resolution)
    {
        return resolution.Width * resolution.Height >= 4032 * 3024
            ? AVCapturePhotoQualityPrioritization.Quality
            : AVCapturePhotoQualityPrioritization.Balanced;
    }

    /// <summary>
    /// 将 AVFoundation 输出的照片按当前设备朝向旋转像素，输出无 EXIF 朝向的 JPEG。
    /// 从 FrameAnalyzer 同步的 _rotationAngle 决定旋转角度（Portrait=90, LandscapeLeft=0, LandscapeRight=180）。
    /// 使用 SkiaSharp SKCodec 解码原始像素（跳过 JPEG 内置的 EXIF 朝向），避免双重旋转。
    /// </summary>
    private static byte[] RotatePhotoData(NSData data, int angle)
    {
        if (angle == 0) return data.ToArray();

        // 通过 SKCodec 解码原始像素（不应用 EXIF 朝向）
        using var codec = SKCodec.Create(new System.IO.MemoryStream(data.ToArray()));
        if (codec == null) return data.ToArray();

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height);
        using var original = new SKBitmap(info);
        if (codec.GetPixels(info, original.GetPixels()) != SKCodecResult.Success)
            return data.ToArray();

        bool swap = angle == 90 || angle == 270;
        int w = swap ? info.Height : info.Width;
        int h = swap ? info.Width : info.Height;

        using var rotated = new SKBitmap(w, h, original.ColorType, original.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(angle);
        canvas.Translate(-info.Width / 2f, -info.Height / 2f);
        canvas.DrawBitmap(original, 0, 0);

        // 重新编码为 JPEG，无 EXIF 朝向（像素已正）
        using var img = SKImage.FromBitmap(rotated);
        using var encData = img.Encode(SKEncodedImageFormat.Jpeg, 100);
        return encData.ToArray();
    }

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

            if (photo.FileDataRepresentation is { } data)
                this.onComplete(data);
            else
                this.onError("Photo data is null (DidFinishProcessingPhoto called but FileDataRepresentation null)");
        }

    }
}
