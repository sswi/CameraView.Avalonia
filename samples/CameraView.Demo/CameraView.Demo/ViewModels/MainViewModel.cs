using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CameraView.Models;
using CameraView.Demo.Services;
using CameraView.Services;

namespace CameraView.Demo.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ICameraProvider cameraProvider;
    private readonly ICameraPermissions cameraPermissions;

    [ObservableProperty]
    private bool _isCameraRunning;

    [ObservableProperty]
    private string _statusText = "按 Start Camera 开始";

    [ObservableProperty]
    private CameraFacing _currentFacing = CameraFacing.Back;

    [ObservableProperty]
    private bool _torchOn;

    [ObservableProperty]
    private bool _debugMode = true;

    [ObservableProperty]
    private FlashMode _selectedFlashMode = FlashMode.Auto;

    [ObservableProperty]
    private PhotoResolution? _selectedResolution;

    [ObservableProperty]
    private float _zoomValue = 1f;

    [ObservableProperty]
    private float _minZoom = 1f;

    [ObservableProperty]
    private float _maxZoom = 5f;

    [ObservableProperty]
    private float _exposureValue;

    [ObservableProperty]
    private string _orientationInfo = "-";

    // Flash mode 枚举值列表（供 UI 绑定）
    public FlashMode[] FlashModes { get; } = [FlashMode.Auto, FlashMode.On, FlashMode.Off];

    // 分辨率列表
    public ObservableCollection<PhotoResolution> Resolutions { get; } = [];

    public IRelayCommand<PhotoCaptureResult?> OnPhotoCapturedCommand { get; }

    public CameraViewControl? CameraControl { get; set; }

    public MainViewModel()
    {
        this.OnPhotoCapturedCommand = new RelayCommand<PhotoCaptureResult?>(OnPhotoCaptured);

        this.cameraProvider = CameraProviderFactory.Create();
        this.cameraPermissions = CameraProviderFactory.CreatePermissions(this.cameraProvider);

        this.cameraProvider.ErrorOccurred += (_, error) =>
        {
            this.StatusText = $"Error: {error}";
        };
    }

    // ========================================================================
    //  属性变更 → 同步到 Provider
    // ========================================================================

    partial void OnSelectedFlashModeChanged(FlashMode value)
    {
        if (this.CameraControl != null)
            this.CameraControl.FlashMode = value;
    }

    partial void OnSelectedResolutionChanged(PhotoResolution? value)
    {
        if (this.CameraControl != null && value != null)
            this.CameraControl.PhotoResolution = value;
    }

    partial void OnZoomValueChanged(float value)
    {
        if (this.CameraControl != null)
            this.CameraControl.RequestZoomFactor = value;
    }

    partial void OnExposureValueChanged(float value)
    {
        if (this.CameraControl != null)
            this.CameraControl.ExposureCompensation = value;
    }

    partial void OnTorchOnChanged(bool value)
    {
        if (this.CameraControl != null)
            this.CameraControl.TorchOn = value;
    }

    // ========================================================================
    //  指令
    // ========================================================================

    [RelayCommand]
    private async Task StartCameraAsync()
    {
        if (this.CameraControl == null) return;

        try
        {
            bool hasPermission = await this.cameraPermissions.CheckPermissionAsync();
            if (!hasPermission)
            {
                this.StatusText = "请求相机权限…";
                hasPermission = await this.cameraPermissions.RequestPermissionAsync();
            }

            if (!hasPermission)
            {
                this.StatusText = "无相机权限，请在系统设置中授予。";
                return;
            }

            this.StatusText = "初始化相机…";
            await this.CameraControl.InitializeCameraAsync(this.cameraProvider);

            this.StatusText = "启动预览…";
            await this.CameraControl.StartCameraAsync();

            this.IsCameraRunning = true;

            // 填充分辨率列表
            this.Resolutions.Clear();
            foreach (var r in this.CameraControl.SupportedResolutions)
                this.Resolutions.Add(r);
            this.SelectedResolution = this.Resolutions.FirstOrDefault();
            if (this.SelectedResolution != null)
                this.CameraControl.PhotoResolution = this.SelectedResolution;

            // 读取缩放范围
            this.MinZoom = this.cameraProvider.MinZoomFactor ?? 1f;
            this.MaxZoom = this.cameraProvider.MaxZoomFactor ?? 5f;
            this.ZoomValue = this.cameraProvider.CurrentZoomFactor ?? 1f;

            this.StatusText = "相机运行中 – 点击对焦，捏合缩放";
        }
        catch (Exception ex)
        {
            this.StatusText = $"启动失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TakePhotoAsync()
    {
        if (this.CameraControl == null) return;
        this.StatusText = "拍照…";
        await this.CameraControl.TakePhotoAsync();
    }

    [RelayCommand]
    private async Task SwitchCameraAsync()
    {
        if (this.CameraControl == null) return;
        await this.CameraControl.SwitchCameraAsync();
        this.CurrentFacing = this.cameraProvider.CurrentFacing;

        // 切换后刷新缩放范围
        this.MinZoom = this.cameraProvider.MinZoomFactor ?? 1f;
        this.MaxZoom = this.cameraProvider.MaxZoomFactor ?? 5f;
        this.ZoomValue = this.cameraProvider.CurrentZoomFactor ?? 1f;
    }

    [RelayCommand]
    private void ToggleDebug()
    {
        this.DebugMode = !this.DebugMode;
        if (this.CameraControl != null)
            this.CameraControl.DebugMode = this.DebugMode;
    }

    [RelayCommand]
    private async Task ToggleTorchAsync()
    {
        if (this.CameraControl == null) return;
        this.TorchOn = !this.TorchOn;
        await this.cameraProvider.SetTorchAsync(this.TorchOn);
    }

    // ========================================================================
    //  拍照回调
    // ========================================================================

    private void OnPhotoCaptured(PhotoCaptureResult? result)
    {
        if (result == null) return;

        if (result.IsSuccess && result.ImageData != null)
        {
            // 保存到应用文档目录（所有平台通用）
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = System.IO.Path.Combine(dir, $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            System.IO.File.WriteAllBytes(path, result.ImageData);
            this.StatusText = $"照片已保存: {result.ImageData.Length / 1024} KB";
            if (PhotoAlbumSaverRegistry.Current != null)
            {
                this.StatusText = $"照片已拍摄: {result.ImageData.Length / 1024} KB，正在写入相册…";
                _ = this.SavePhotoToAlbumAsync(result.ImageData);
            }
        }
        else
        {
            this.StatusText = $"拍照失败: {result.ErrorMessage ?? "未知"}";
        }
    }

    // ========================================================================
    //  朝向更新（由 UI 定时轮询或事件触发）
    // ========================================================================

    public void UpdateOrientation(CameraViewControl control)
    {
        var ori = control.DeviceOrientation;
        if (ori != null)
        {
            this.OrientationInfo =
                $"Pitch {ori.Pitch,6:F1}°  Roll {ori.Roll,6:F1}°  Yaw {ori.Yaw,6:F1}°  {ori.StateLabel}";
        }
    }

    private async Task SavePhotoToAlbumAsync(byte[] imageBytes)
    {
        try
        {
            var photoAlbumSaver = PhotoAlbumSaverRegistry.Current;
            if (photoAlbumSaver == null)
            {
                return;
            }

            var saveError = await photoAlbumSaver.SavePhotoAsync(imageBytes).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                this.StatusText = saveError == null ? "照片已保存到相册" : $"相册写入失败: {saveError}";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                this.StatusText = $"相册写入异常: {ex.Message}";
            });
        }
    }
}
