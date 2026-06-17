using Android;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using AndroidX.Camera.Core;
using AndroidX.Core.App;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Activity = Android.App.Activity;
using CameraManager = Android.Hardware.Camera2.CameraManager;
using StreamConfigurationMap = Android.Hardware.Camera2.Params.StreamConfigurationMap;

namespace CameraView.Platforms.Android;

public class AndroidCameraProvider : Services.ICameraProvider, ICameraPermissions, ICameraActivityAware, ICameraOrientationAware
{
    private readonly Context appContext;
    private Activity? currentActivity;
    private TaskCompletionSource<bool>? pendingPermissionRequest;

    private ProcessCameraProvider? cameraProvider;
    private ImageCapture? imageCapture;
    private FrameAnalyzer? frameAnalyzer;
    private ICamera? camera;
    private CameraSelector? cameraSelector;

    private int lensFacing = CameraSelector.LensFacingBack;
    private DeviceOrientationState deviceOrientation = DeviceOrientationState.PortraitUpright;

    public bool IsInitialized => this.cameraProvider != null;
    public CameraFacing CurrentFacing => this.lensFacing == CameraSelector.LensFacingBack
        ? CameraFacing.Back : CameraFacing.Front;
    public float? MinZoomFactor { get; private set; }
    public float? MaxZoomFactor { get; private set; }
    public float? CurrentZoomFactor { get; private set; }
    public CameraView.Models.FlashMode FlashMode { get; private set; } = CameraView.Models.FlashMode.Auto;
    private List<PhotoResolution> supportedPhotoResolutions = [.. PhotoResolution.DefaultPresets];

    public PhotoResolution PhotoResolution { get; private set; } = PhotoResolution.DefaultPresets[0];
    public IReadOnlyList<PhotoResolution> SupportedPhotoResolutions => this.supportedPhotoResolutions;
    public float MinExposureCompensation { get; private set; }
    public float MaxExposureCompensation { get; private set; }
    public float ExposureCompensation { get; private set; }

    public ICameraPermissions Permissions => this;

    public event EventHandler<byte[]>? PhotoCaptured;
    public event EventHandler<SKBitmap>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;

    public AndroidCameraProvider(Context context)
    {
        this.appContext = context.ApplicationContext ?? context;
    }

    public void SetActivity(object activity)
    {
        this.currentActivity = activity as Activity;
    }

    /// <summary>由 CameraViewControl 调用，使用重力传感器方向更新设备朝向</summary>
    public void UpdateDeviceOrientation(DeviceOrientationState state)
    {
        this.deviceOrientation = state;
    }

    // --- ICameraPermissions ---

    public Task<bool> CheckPermissionAsync()
    {
        var ctx = this.currentActivity ?? this.appContext;
        var result = ContextCompat.CheckSelfPermission(ctx, Manifest.Permission.Camera);
        return Task.FromResult(result == Permission.Granted);
    }

    public async Task<bool> RequestPermissionAsync()
    {
        if (this.currentActivity == null)
            return await this.CheckPermissionAsync();

        if (await this.CheckPermissionAsync())
            return true;

        try
        {
            this.pendingPermissionRequest = new TaskCompletionSource<bool>();
            ActivityCompat.RequestPermissions(
                this.currentActivity,
                [Manifest.Permission.Camera],
                CameraPermissionRequestCode);
            return await this.pendingPermissionRequest.Task;
        }
        catch
        {
            this.pendingPermissionRequest = null;
            return false;
        }
    }

    internal const int CameraPermissionRequestCode = 9527;

    internal void NotifyPermissionResult(bool granted)
    {
        if (this.pendingPermissionRequest != null)
        {
            this.pendingPermissionRequest.TrySetResult(granted);
            this.pendingPermissionRequest = null;
        }
    }

    // --- ICameraProvider ---

    public Task InitializeAsync(CameraOptions? options = null)
    {
        if (options?.CameraFacing == CameraFacing.Front)
            this.lensFacing = CameraSelector.LensFacingFront;

        this.UpdateSupportedPhotoResolutions();
        this.PhotoResolution = this.GetBestSupportedPhotoResolution(this.PhotoResolution);

        var tcs = new TaskCompletionSource();
        var future = ProcessCameraProvider.GetInstance(this.appContext);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            try
            {
                this.cameraProvider = (ProcessCameraProvider)future.Get();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }), ContextCompat.GetMainExecutor(this.appContext));

        return tcs.Task;
    }

    public Task StartPreviewAsync()
    {
        if (this.cameraProvider == null)
            throw new InvalidOperationException("Camera not initialized.");

        // Unbind previous camera if any
        try { this.cameraProvider.UnbindAll(); } catch { }

        this.cameraSelector = new CameraSelector.Builder()
            .RequireLensFacing(this.lensFacing)
            .Build();

        this.frameAnalyzer = new FrameAnalyzer(frame =>
        {
            this.FrameReceived?.Invoke(this, frame);
        });

        this.UpdateSupportedPhotoResolutions();
        this.PhotoResolution = this.GetBestSupportedPhotoResolution(this.PhotoResolution);
        var resolution = this.PhotoResolution;
        var imageCaptureBuilder = new ImageCapture.Builder()
            .SetCaptureMode(ImageCapture.CaptureModeMaximizeQuality)
            .SetTargetResolution(new global::Android.Util.Size(resolution.Width, resolution.Height));

        // Set target rotation based on current display orientation
        try
        {
            var wm = this.appContext.GetSystemService(Context.WindowService);
            if (wm is global::Android.Views.IWindowManager wmgr)
            {
                var displayRotation = (int)wmgr.DefaultDisplay!.Rotation!;
                imageCaptureBuilder.SetTargetRotation(displayRotation);
            }
        }
        catch { }

        this.imageCapture = imageCaptureBuilder.Build();

        var imageAnalysis = new ImageAnalysis.Builder()
            .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
            .Build();
        imageAnalysis.SetAnalyzer(
            ContextCompat.GetMainExecutor(this.appContext),
            this.frameAnalyzer);

        try
        {
            var lifecycleOwner = new ForeverLifecycleOwner();
            this.camera = this.cameraProvider.BindToLifecycle(
                lifecycleOwner,
                this.cameraSelector,
                this.imageCapture,
                imageAnalysis);

            // Observe zoom state (use ForeverLifecycleOwner since we don't have AppCompatActivity)
            if (this.camera?.CameraInfo?.ZoomState != null)
            {
                this.camera.CameraInfo.ZoomState.Observe(
                    lifecycleOwner,
                    new ZoomObserver(state =>
                    {
                        this.MinZoomFactor = MathF.Round(state.MinZoomRatio, 1);
                        this.MaxZoomFactor = MathF.Round(state.MaxZoomRatio, 1);
                        this.CurrentZoomFactor = MathF.Round(state.ZoomRatio, 1);
                    }));
            }
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"Failed to start preview: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task StopPreviewAsync()
    {
        this.cameraProvider?.UnbindAll();
        return Task.CompletedTask;
    }

    public Task TakePhotoAsync()
    {
        if (this.imageCapture == null)
            throw new InvalidOperationException("Camera preview not started.");

        var outputDir = this.appContext.GetExternalFilesDir(null)
            ?? this.appContext.FilesDir;

        var outputFile = new Java.IO.File(
            outputDir,
            $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

        var outputOptions = new ImageCapture.OutputFileOptions.Builder(outputFile).Build();

        // 在按快门瞬间捕获设备方向，避免回调时方向已变（同 iOS 做法）
        var capturedOrientation = this.deviceOrientation;

        this.imageCapture.TakePicture(
            outputOptions,
            ContextCompat.GetMainExecutor(this.appContext),
            new PhotoCaptureCallback(
                result =>
                {
                    try
                    {
                        var bytes = System.IO.File.ReadAllBytes(outputFile.AbsolutePath);
                        var photoAngle = GetPhotoRotationAngle(capturedOrientation);
                        var rotated = RotatePhotoData(bytes, photoAngle);
                        this.PhotoCaptured?.Invoke(this, rotated);
                    }
                    catch (Exception ex)
                    {
                        this.ErrorOccurred?.Invoke(this, $"Failed to read photo: {ex.Message}");
                    }
                },
                error => this.ErrorOccurred?.Invoke(this, $"Photo error: {error}")));

        return Task.CompletedTask;
    }

    public Task SwitchCameraAsync(CameraFacing facing)
    {
        this.lensFacing = facing == CameraFacing.Back
            ? CameraSelector.LensFacingBack
            : CameraSelector.LensFacingFront;

        if (this.cameraProvider != null)
        {
            this.cameraProvider.UnbindAll();
            return this.StartPreviewAsync();
        }

        return Task.CompletedTask;
    }

    public Task SetFocusAsync(float normalizedX, float normalizedY)
    {
        if (this.camera?.CameraControl == null) return Task.CompletedTask;

        try
        {
            var factory = new global::AndroidX.Camera.Core.SurfaceOrientedMeteringPointFactory(1f, 1f);
            var point = factory.CreatePoint(normalizedX, normalizedY, 0.15f);
            var action = new FocusMeteringAction.Builder(point, FocusMeteringAction.FlagAf | FocusMeteringAction.FlagAe)
                .SetAutoCancelDuration(3, Java.Util.Concurrent.TimeUnit.Seconds!)
                .Build();
            this.camera.CameraControl.StartFocusAndMetering(action);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Focus failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task SetZoomAsync(float zoomFactor)
    {
        if (this.camera?.CameraControl == null) return Task.CompletedTask;
        try
        {
            var clamped = Math.Clamp(zoomFactor, this.MinZoomFactor ?? 1f, this.MaxZoomFactor ?? 5f);
            this.camera.CameraControl.SetZoomRatio(clamped);
            this.CurrentZoomFactor = clamped;
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"Zoom failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public Task SetTorchAsync(bool on)
    {
        if (this.camera?.CameraControl == null) return Task.CompletedTask;
        try
        {
            this.camera.CameraControl.EnableTorch(on);
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"Torch failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public Task SetPhotoResolutionAsync(PhotoResolution resolution)
    {
        this.PhotoResolution = resolution;

        if (this.cameraProvider != null && this.IsInitialized)
        {
            this.cameraProvider.UnbindAll();
            return this.StartPreviewAsync();
        }

        return Task.CompletedTask;
    }

    public Task SetExposureCompensationAsync(float ev)
    {
        if (this.camera?.CameraControl == null) return Task.CompletedTask;

        try
        {
            var exposureState = this.camera.CameraInfo?.ExposureState;
            if (exposureState == null) return Task.CompletedTask;

            // Query range on first use
            if (this.MinExposureCompensation == 0 && this.MaxExposureCompensation == 0)
            {
                this.MinExposureCompensation = ((Java.Lang.Integer)exposureState.ExposureCompensationRange.Lower!).IntValue();
                this.MaxExposureCompensation = ((Java.Lang.Integer)exposureState.ExposureCompensationRange.Upper!).IntValue();
            }

            // Convert EV to integer index
            var rational = (global::Android.Util.Rational)exposureState.ExposureCompensationStep!;
            float step = (float)rational.Numerator / rational.Denominator;
            int index = step > 0 ? (int)Math.Round(ev / step) : 0;
            int lower = ((Java.Lang.Integer)exposureState.ExposureCompensationRange.Lower!).IntValue();
            int upper = ((Java.Lang.Integer)exposureState.ExposureCompensationRange.Upper!).IntValue();
            index = Math.Clamp(index, lower, upper);

            this.camera.CameraControl.SetExposureCompensationIndex(index);
            this.ExposureCompensation = index * step;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Exposure failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task SetFlashModeAsync(CameraView.Models.FlashMode mode)
    {
        this.FlashMode = mode;

        // Set flash mode on ImageCapture for photo capture
        if (this.imageCapture != null)
        {
            try
            {
                this.imageCapture.FlashMode = mode switch
                {
                    CameraView.Models.FlashMode.Off => ImageCapture.FlashModeOff,
                    CameraView.Models.FlashMode.On => ImageCapture.FlashModeOn,
                    CameraView.Models.  FlashMode.Auto => ImageCapture.FlashModeAuto,
                    _ => ImageCapture.FlashModeAuto
                };
            }
            catch (Exception ex)
            {
                this.ErrorOccurred?.Invoke(this, $"Flash mode failed: {ex.Message}");
            }
        }

        // For FlashMode.On, also enable torch during preview
        if (this.camera?.CameraControl != null)
        {
            try
            {
                this.camera.CameraControl.EnableTorch(mode == CameraView.Models.FlashMode.On);
            }
            catch { }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        this.cameraProvider?.UnbindAll();
        this.cameraProvider?.Dispose();
    }

    // ======== 照片方向校正（同 iOS RotatePhotoData） ========

    /// <summary>设备朝向 → 照片旋转角度（与 iOS 一致）</summary>
    private static int GetPhotoRotationAngle(DeviceOrientationState orientation)
    {
        return orientation switch
        {
            DeviceOrientationState.PortraitUpright => 90,     // 竖屏正拿
            DeviceOrientationState.LandscapeLeft => 0,         // 朝左横屏
            DeviceOrientationState.PortraitUpsideDown => 270,  // 倒立
            DeviceOrientationState.LandscapeRight => 180,      // 朝右横屏
            DeviceOrientationState.FlatFaceDown => 180,        // 平放朝下
            DeviceOrientationState.FlatFaceUp => 0,            // 平放朝上
            _ => 90
        };
    }

    /// <summary>用 SkiaSharp 物理旋转 JPEG 像素（同 iOS RotatePhotoData）</summary>
    private static byte[] RotatePhotoData(byte[] jpegData, int angle)
    {
        if (angle == 0) return jpegData;

        using var codec = SKCodec.Create(new System.IO.MemoryStream(jpegData));
        if (codec == null) return jpegData;

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height);
        using var original = new SKBitmap(info);
        if (codec.GetPixels(info, original.GetPixels()) != SKCodecResult.Success)
            return jpegData;

        bool swap = angle == 90 || angle == 270;
        int w = swap ? info.Height : info.Width;
        int h = swap ? info.Width : info.Height;

        using var rotated = new SKBitmap(w, h, original.ColorType, original.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(angle);
        canvas.Translate(-info.Width / 2f, -info.Height / 2f);
        canvas.DrawBitmap(original, 0, 0);

        using var img = SKImage.FromBitmap(rotated);
        using var encData = img.Encode(SKEncodedImageFormat.Jpeg, 100);
        return encData.ToArray();
    }

    // ======== Resolution query (Camera2) ========

    private void UpdateSupportedPhotoResolutions()
    {
        try
        {
            var cameraManager = this.appContext.GetSystemService(Context.CameraService) as CameraManager;
            var cameraId = ResolveCameraIdForCurrentFacing(cameraManager);
            if (cameraManager == null || string.IsNullOrWhiteSpace(cameraId))
            {
                this.supportedPhotoResolutions = [.. PhotoResolution.DefaultPresets];
                return;
            }
      
            var characteristics = cameraManager.GetCameraCharacteristics(cameraId);
            var map = characteristics?.Get(CameraCharacteristics.ScalerStreamConfigurationMap)
                as StreamConfigurationMap;
            var sizes = map?.GetOutputSizes((int)ImageFormatType.Jpeg);
            var resolutions = sizes?
                .Where(size => size != null && size.Width > 0 && size.Height > 0)
                .Select(size => new PhotoResolution(size!.Width, size.Height, CreatePhotoResolutionLabel(size.Width, size.Height)))
                .DistinctBy(r => (r.Width, r.Height))
                .OrderByDescending(r => r.Width * r.Height)
                .ThenByDescending(r => r.Width)
                .ToList();

            this.supportedPhotoResolutions = resolutions is { Count: > 0 }
                ? resolutions
                : [.. PhotoResolution.DefaultPresets];
        }
        catch
        {
            this.supportedPhotoResolutions = [.. PhotoResolution.DefaultPresets];
        }
    }

    private string? ResolveCameraIdForCurrentFacing(CameraManager? cameraManager)
    {
        if (cameraManager == null) return null;
        foreach (var id in cameraManager.GetCameraIdList())
        {
            try
            {
                var chars = cameraManager.GetCameraCharacteristics(id);
                var facing = chars?.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer;
                if (facing?.IntValue() == this.lensFacing) return id;
            }
            catch { }
        }
        return cameraManager.GetCameraIdList().FirstOrDefault();
    }

    private PhotoResolution GetBestSupportedPhotoResolution(PhotoResolution preferred)
    {
        if (this.supportedPhotoResolutions.Count == 0) return preferred;
        var exact = this.supportedPhotoResolutions.FirstOrDefault(r => r.Width == preferred.Width && r.Height == preferred.Height);
        if (exact != null) return exact;
        var pixels = preferred.Width * preferred.Height;
        return this.supportedPhotoResolutions
            .OrderBy(r => Math.Abs((r.Width * r.Height) - pixels))
            .ThenBy(r => Math.Abs(((double)r.Width / r.Height) - preferred.AspectRatio))
            .First();
    }

    private static string CreatePhotoResolutionLabel(int w, int h)
    {
        var ratio = (double)w / h;
        var mp = w * h / 1_000_000d;
        var label = Math.Abs(ratio - 4d / 3d) < 0.03 ? "4:3"
            : Math.Abs(ratio - 16d / 9d) < 0.03 ? "16:9"
            : Math.Abs(ratio - 1d) < 0.03 ? "1:1"
            : $"{ratio:F2}:1";
        return $"{label} {mp:F1}MP";
    }

}
