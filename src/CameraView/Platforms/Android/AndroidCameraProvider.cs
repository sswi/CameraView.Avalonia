using System.Diagnostics;
using Activity = Android.App.Activity;
using AndroidX.Camera.Core;

namespace CameraView.Platforms.Android;

public class AndroidCameraProvider : Services.ICameraProvider, ICameraPermissions, ICameraActivityAware
{
    private readonly Context appContext;
    private Activity? currentActivity;
    private List<PhotoResolution> supportedPhotoResolutions = [.. PhotoResolution.DefaultPresets];

    private ProcessCameraProvider? cameraProvider;
    private ImageCapture? imageCapture;
    private FrameAnalyzer? frameAnalyzer;
    private ICamera? camera;
    private CameraSelector? cameraSelector;

    private int lensFacing = CameraSelector.LensFacingBack;

    public bool IsInitialized => this.cameraProvider != null;
    public CameraFacing CurrentFacing => this.lensFacing == CameraSelector.LensFacingBack
        ? CameraFacing.Back : CameraFacing.Front;
    public float? MinZoomFactor { get; private set; }
    public float? MaxZoomFactor { get; private set; }
    public float? CurrentZoomFactor { get; private set; }
    public FlashMode FlashMode { get; private set; } = FlashMode.Auto;
    public PhotoResolution PhotoResolution { get; private set; } = PhotoResolution.DefaultPresets[0]; // 4032x3024
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
        this.UpdateSupportedPhotoResolutions();
    }

    public void SetActivity(object activity)
    {
        this.currentActivity = activity as Activity;
        this.UpdateSupportedPhotoResolutions();
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
            ActivityCompat.RequestPermissions(
                this.currentActivity,
                [Manifest.Permission.Camera],
                0);
            return false;
        }
        catch
        {
            return false;
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

        this.imageCapture.TakePicture(
            outputOptions,
            ContextCompat.GetMainExecutor(this.appContext),
            new PhotoCaptureCallback(
                result =>
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(outputFile.AbsolutePath);
                        this.PhotoCaptured?.Invoke(this, bytes);
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
        this.UpdateSupportedPhotoResolutions();
        this.PhotoResolution = this.GetBestSupportedPhotoResolution(this.PhotoResolution);

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
        this.UpdateSupportedPhotoResolutions();
        this.PhotoResolution = this.GetBestSupportedPhotoResolution(resolution);

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

    public Task SetFlashModeAsync(FlashMode mode)
    {
        this.FlashMode = mode;

        // Set flash mode on ImageCapture for photo capture
        if (this.imageCapture != null)
        {
            try
            {
                this.imageCapture.FlashMode = mode switch
                {
                    FlashMode.Off => ImageCapture.FlashModeOff,
                    FlashMode.On => ImageCapture.FlashModeOn,
                    FlashMode.Auto => ImageCapture.FlashModeAuto,
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
                this.camera.CameraControl.EnableTorch(mode == FlashMode.On);
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

    private void UpdateSupportedPhotoResolutions()
    {
        try
        {
            var cameraManager = this.appContext.GetSystemService(Context.CameraService) as global::Android.Hardware.Camera2.CameraManager;
            var cameraId = this.ResolveCameraIdForCurrentFacing(cameraManager);
            if (cameraManager == null || string.IsNullOrWhiteSpace(cameraId))
            {
                this.supportedPhotoResolutions = [.. PhotoResolution.DefaultPresets];
                return;
            }

            var characteristics = cameraManager.GetCameraCharacteristics(cameraId);
            var map = characteristics?.Get(global::Android.Hardware.Camera2.CameraCharacteristics.ScalerStreamConfigurationMap)
                as global::Android.Hardware.Camera2.Params.StreamConfigurationMap;
            var sizes = map?.GetOutputSizes((int)global::Android.Graphics.ImageFormatType.Jpeg);
            var resolutions = sizes?
                .Where(size => size != null && size.Width > 0 && size.Height > 0)
                .Select(size => new PhotoResolution(size!.Width, size.Height, CreatePhotoResolutionLabel(size.Width, size.Height)))
                .DistinctBy(resolution => (resolution.Width, resolution.Height))
                .OrderByDescending(resolution => resolution.Width * resolution.Height)
                .ThenByDescending(resolution => resolution.Width)
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

    private string? ResolveCameraIdForCurrentFacing(global::Android.Hardware.Camera2.CameraManager? cameraManager)
    {
        if (cameraManager == null)
            return null;

        foreach (var cameraId in cameraManager.GetCameraIdList())
        {
            try
            {
                var characteristics = cameraManager.GetCameraCharacteristics(cameraId);
                var facing = characteristics?.Get(global::Android.Hardware.Camera2.CameraCharacteristics.LensFacing)
                    as Java.Lang.Integer;
                if (facing?.IntValue() == this.lensFacing)
                    return cameraId;
            }
            catch
            {
            }
        }

        return cameraManager.GetCameraIdList().FirstOrDefault();
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

}
