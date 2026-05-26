using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CameraView.Models;
using CameraView.Services;

namespace CameraView.Demo.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ICameraProvider cameraProvider;
    private readonly ICameraPermissions cameraPermissions;

    [ObservableProperty]
    private bool _isCameraRunning;

    [ObservableProperty]
    private string _statusText = "Tap Start Camera to begin";

    [ObservableProperty]
    private CameraFacing _currentFacing = CameraFacing.Back;

    [ObservableProperty]
    private bool _torchOn;

    [ObservableProperty]
    private bool _debugMode = true;

    public IRelayCommand<PhotoCaptureResult?> OnPhotoCapturedCommand { get; }

    public CameraViewControl? CameraControl { get; set; }

    public MainViewModel()
    {
        this.OnPhotoCapturedCommand = new RelayCommand<PhotoCaptureResult?>(OnPhotoCaptured);

        // Create the platform camera provider (pass null for default context)
        this.cameraProvider = CameraProviderFactory.Create();
        this.cameraPermissions = CameraProviderFactory.CreatePermissions(this.cameraProvider);

        this.cameraProvider.ErrorOccurred += (_, error) =>
        {
            this.StatusText = $"Error: {error}";
        };
    }

    [RelayCommand]
    private async Task StartCameraAsync()
    {
        if (this.CameraControl == null) return;

        try
        {
            // Step 1: Check camera permission
            bool hasPermission = await this.cameraPermissions.CheckPermissionAsync();
            if (!hasPermission)
            {
                this.StatusText = "Requesting camera permission...";
                hasPermission = await this.cameraPermissions.RequestPermissionAsync();
            }

            if (!hasPermission)
            {
                this.StatusText = "Camera permission denied. Please grant camera permission in Settings.";
                return;
            }

            // Step 2: Initialize and start
            this.StatusText = "Initializing camera...";
            await this.CameraControl.InitializeCameraAsync(this.cameraProvider);

            this.StatusText = "Starting preview...";
            await this.CameraControl.StartCameraAsync();

            this.IsCameraRunning = true;
            this.StatusText = "Camera running - tap to focus, pinch to zoom";
        }
        catch (Exception ex)
        {
            this.StatusText = $"Failed to start camera: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TakePhotoAsync()
    {
        if (this.CameraControl == null) return;
        this.StatusText = "Taking photo...";
        await this.CameraControl.TakePhotoAsync();
    }

    [RelayCommand]
    private async Task SwitchCameraAsync()
    {
        if (this.CameraControl == null) return;
        await this.CameraControl.SwitchCameraAsync();
        this.CurrentFacing = this.cameraProvider.CurrentFacing;
    }

    [RelayCommand]
    private void ToggleDebug()
    {
        this.DebugMode = !this.DebugMode;
        if (this.CameraControl != null)
            this.CameraControl.DebugMode = this.DebugMode;
    }

    private void OnPhotoCaptured(PhotoCaptureResult? result)
    {
        if (result == null) return;

        if (result.IsSuccess && result.ImageData != null)
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            System.IO.File.WriteAllBytes(path, result.ImageData);
            this.StatusText = $"Photo saved: {result.ImageData.Length / 1024} KB";
        }
        else
        {
            this.StatusText = $"Photo failed: {result.ErrorMessage ?? "Unknown"}";
        }
    }

    [RelayCommand]
    private async Task ToggleTorchAsync()
    {
        this.TorchOn = !this.TorchOn;
        await this.cameraProvider.SetTorchAsync(this.TorchOn);
    }
}
