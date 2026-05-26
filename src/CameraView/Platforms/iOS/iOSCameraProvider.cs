using AVFoundation;
using CoreGraphics;
using CoreMedia;
using CoreVideo;
using CameraView.Models;
using CameraView.Services;
using CameraView.Utils;
using SkiaSharp;
using UIKit;

namespace CameraView.Platforms.iOS;

internal class iOSCameraProvider : ICameraProvider
{
    private readonly AsyncLock updateCameraLock = new();

    private AVCaptureSession? session;
    private AVCaptureDevice? captureDevice;
    private AVCaptureDeviceInput? captureInput;
    private AVCaptureVideoDataOutput? videoDataOutput;
    private AVCapturePhotoOutput? photoOutput;
    private FrameAnalyzer? frameAnalyzer;

    private bool started;
    private bool disposed;
    private CameraFacing currentFacing = CameraFacing.Back;

    public bool IsInitialized => this.session != null;
    public CameraFacing CurrentFacing => this.currentFacing;
    public float? MinZoomFactor { get; private set; }
    public float? MaxZoomFactor { get; private set; }
    public float? CurrentZoomFactor { get; private set; }
    public FlashMode FlashMode { get; private set; } = FlashMode.Auto;
    public PhotoResolution PhotoResolution { get; private set; } = PhotoResolution.DefaultPresets[3];
    public IReadOnlyList<PhotoResolution> SupportedPhotoResolutions => PhotoResolution.DefaultPresets;

    public Task SetPhotoResolutionAsync(PhotoResolution resolution)
    {
        this.PhotoResolution = resolution;
        // iOS uses AVCapturePhotoSettings to match - applied at capture time
        return Task.CompletedTask;
    }

    public event EventHandler<byte[]>? PhotoCaptured;
    public event EventHandler<SKBitmap>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;

    public Task InitializeAsync(CameraOptions? options = null)
    {
        if (options?.CameraFacing == CameraFacing.Front)
            this.currentFacing = CameraFacing.Front;

        this.session = new AVCaptureSession();
        this.videoDataOutput = new AVCaptureVideoDataOutput
        {
            AlwaysDiscardsLateVideoFrames = true
        };

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
                this.UpdateMinMaxZoom();

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
        var delegateHandler = new PhotoCaptureDelegate(
            data => this.PhotoCaptured?.Invoke(this, data.ToArray()),
            error => this.ErrorOccurred?.Invoke(this, error));

        this.photoOutput.CapturePhoto(settings, delegateHandler);

        return Task.CompletedTask;
    }

    public async Task SwitchCameraAsync(CameraFacing facing)
    {
        this.currentFacing = facing;
        using (await this.updateCameraLock.LockAsync())
        {
            await this.UpdateCameraAsync();
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

    public Task SetFlashModeAsync(FlashMode mode)
    {
        this.FlashMode = mode;

        // Store flash mode for next photo capture — applied in TakePhotoAsync
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        this.disposed = true;
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
            // Remove old input
            if (this.captureInput != null && this.session.Inputs.Contains(this.captureInput))
            {
                this.session.RemoveInput(this.captureInput);
                this.captureInput.Dispose();
            }

            // Find camera device
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

            // Add video data output
            if (!this.session.Outputs.Contains(this.videoDataOutput) &&
                this.session.CanAddOutput(this.videoDataOutput))
            {
                this.session.AddOutput(this.videoDataOutput);
            }

            // Add photo output
            if (!this.session.Outputs.Contains(this.photoOutput) &&
                this.session.CanAddOutput(this.photoOutput))
            {
                this.session.AddOutput(this.photoOutput);
            }

            this.session.SessionPreset = AVCaptureSession.Preset1280x720;
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

    private void UpdateMinMaxZoom()
    {
        if (this.captureDevice == null) return;

        this.MinZoomFactor = MathF.Round((float)this.captureDevice.MinAvailableVideoZoomFactor, 1);
        this.MaxZoomFactor = MathF.Round((float)this.captureDevice.MaxAvailableVideoZoomFactor, 1);
        this.CurrentZoomFactor = MathF.Round((float)this.captureDevice.VideoZoomFactor, 1);
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

            var data = photo.FileDataRepresentation;
            if (data != null)
                this.onComplete(data);
        }
    }
}
