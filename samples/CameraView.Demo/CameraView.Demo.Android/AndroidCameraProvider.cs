using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using CameraView.Models;
using CameraView.Services;
using SkiaSharp;

namespace CameraView.Demo.Android;

internal class AndroidCameraProvider : Services.ICameraProvider, ICameraPermissions, ICameraActivityAware
{
    private readonly Context appContext;
    private global::Android.App.Activity? currentActivity;

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
    public PhotoResolution PhotoResolution { get; private set; } = PhotoResolution.DefaultPresets[3]; // 1920x1080
    public IReadOnlyList<PhotoResolution> SupportedPhotoResolutions => PhotoResolution.DefaultPresets;

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
        this.currentActivity = activity as global::Android.App.Activity;
    }

    // --- ICameraPermissions ---

    public Task<bool> CheckPermissionAsync()
    {
        var ctx = this.currentActivity ?? this.appContext;
        var result = ContextCompat.CheckSelfPermission(ctx, global::Android.Manifest.Permission.Camera);
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
            global::AndroidX.Core.App.ActivityCompat.RequestPermissions(
                this.currentActivity,
                [global::Android.Manifest.Permission.Camera],
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

        if (this.cameraProvider != null)
        {
            this.cameraProvider.UnbindAll();
            return this.StartPreviewAsync();
        }

        return Task.CompletedTask;
    }

    public Task SetFocusAsync(float normalizedX, float normalizedY)
    {
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

        // Restart preview to apply new resolution
        if (this.cameraProvider != null && this.IsInitialized)
        {
            this.cameraProvider.UnbindAll();
            return this.StartPreviewAsync();
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

    /// <summary>
    /// Minimal ILifecycleOwner that reports the lifecycle as always started.
    /// </summary>
    private class ForeverLifecycleOwner : Java.Lang.Object, ILifecycleOwner
    {
        private readonly LifecycleRegistry registry;

        public ForeverLifecycleOwner()
        {
            this.registry = new LifecycleRegistry(this);
            this.registry.HandleLifecycleEvent(Lifecycle.Event.OnStart);
            this.registry.HandleLifecycleEvent(Lifecycle.Event.OnResume);
        }

        public Lifecycle Lifecycle => this.registry;
    }

    /// <summary>
    /// Observes CameraX IZoomState changes and updates the provider's zoom properties.
    /// </summary>
    private class ZoomObserver : Java.Lang.Object, IObserver
    {
        private readonly Action<IZoomState> onChanged;

        public ZoomObserver(Action<IZoomState> onChanged)
        {
            this.onChanged = onChanged;
        }

        public void OnChanged(Java.Lang.Object? value)
        {
            if (value is IZoomState state)
            {
                this.onChanged(state);
            }
        }
    }
}
