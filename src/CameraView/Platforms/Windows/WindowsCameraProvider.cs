#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using CameraView.Models;
using CameraView.Services;
using SkiaSharp;

namespace CameraView.Platforms.Windows;

/// <summary>
/// Windows 相机提供者 — 基于 WinRT MediaCapture + MediaFrameReader
/// 不支持的硬件功能静默忽略，不抛异常
/// </summary>
internal class WindowsCameraProvider : ICameraProvider
{
    private MediaCapture? mediaCapture;
    private MediaFrameReader? frameReader;
    private bool started;
    private List<DeviceInformation>? cameraDevices;
    private int currentDeviceIndex;

    public bool IsInitialized => this.mediaCapture != null;
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
    //  初始化 & 预览
    // ========================================================================

    public async Task InitializeAsync(CameraOptions? options = null)
    {
        if (options?.CameraFacing == CameraFacing.Front)
            CurrentFacing = CameraFacing.Front;

        try
        {
            this.cameraDevices = (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).ToList();
            if (this.cameraDevices.Count == 0)
            {
                this.ErrorOccurred?.Invoke(this, "未找到摄像头设备。");
                return;
            }

            // 按 facing 或索引选择初始设备
            var initialIndex = this.cameraDevices.FindIndex(d =>
                d.EnclosureLocation?.Panel == (CurrentFacing == CameraFacing.Back
                    ? global::Windows.Devices.Enumeration.Panel.Back
                    : global::Windows.Devices.Enumeration.Panel.Front));
            this.currentDeviceIndex = initialIndex >= 0 ? initialIndex : 0;

            await InitDeviceAsync(this.currentDeviceIndex);
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"初始化失败: {ex.Message}");
        }
    }

    private async Task InitDeviceAsync(int index)
    {
        if (this.cameraDevices == null || index < 0 || index >= this.cameraDevices.Count)
            return;

        this.mediaCapture?.Dispose();
        this.mediaCapture = new MediaCapture();
        await this.mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = this.cameraDevices[index].Id,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        });

        this.currentDeviceIndex = index;
        CurrentFacing = this.cameraDevices[index].EnclosureLocation?.Panel
            == global::Windows.Devices.Enumeration.Panel.Front
            ? CameraFacing.Front : CameraFacing.Back;

        ReadCapabilities();
    }

    public async Task StartPreviewAsync()
    {
        if (this.mediaCapture == null) return;

        try
        {
            // 取第一个可用帧源（不过滤，兼容最多硬件）
            var source = this.mediaCapture.FrameSources.Values.FirstOrDefault();
            if (source == null)
            {
                this.ErrorOccurred?.Invoke(this, "未找到视频源。");
                return;
            }

            this.frameReader?.Dispose();
            this.frameReader = await this.mediaCapture.CreateFrameReaderAsync(source);
            this.frameReader.FrameArrived += OnFrameArrived;

            var status = await this.frameReader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                this.ErrorOccurred?.Invoke(this, $"帧读取器启动失败: {status}");
                return;
            }

            this.started = true;
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"启动预览失败: {ex.Message}");
        }
    }

    public Task StopPreviewAsync()
    {
        if (this.frameReader != null)
        {
            this.frameReader.FrameArrived -= OnFrameArrived;
            _ = this.frameReader.StopAsync();
        }
        this.started = false;
        return Task.CompletedTask;
    }

    // ========================================================================
    //  拍照
    // ========================================================================

    public async Task TakePhotoAsync()
    {
        if (this.mediaCapture == null) return;

        try
        {
            var stream = new InMemoryRandomAccessStream();
            await this.mediaCapture.CapturePhotoToStreamAsync(
                ImageEncodingProperties.CreateJpeg(), stream);

            using var inputStream = stream.GetInputStreamAt(0);
            using var reader = new DataReader(inputStream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);

            this.PhotoCaptured?.Invoke(this, bytes);
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"拍照失败: {ex.Message}");
        }
    }

    // ========================================================================
    //  切换摄像头
    // ========================================================================

    public async Task SwitchCameraAsync(CameraFacing facing)
    {
        // 桌面端多摄像头：循环切换（忽略 facing）
        if (this.cameraDevices == null || this.cameraDevices.Count <= 1)
            return;

        var wasRunning = this.started;
        await StopPreviewAsync();

        var nextIndex = (this.currentDeviceIndex + 1) % this.cameraDevices.Count;
        await InitDeviceAsync(nextIndex);

        if (wasRunning && this.mediaCapture != null)
            await StartPreviewAsync();
    }

    // ========================================================================
    //  不支持的硬件功能 — 全部静默忽略（不抛异常）
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
        this.PhotoResolution = resolution;
        // Windows 摄像头可在启动前设置分辨率，这里仅记录偏好
        return Task.CompletedTask;
    }

    public Task SetExposureCompensationAsync(float ev)
    {
        this.ExposureCompensation = ev;
        return Task.CompletedTask;
    }

    // ========================================================================
    //  帧处理（AOT 安全：CopyToBuffer + DataReader，免 COM 接口）
    // ========================================================================

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        MediaFrameReference? frame = null;
        try { frame = sender.TryAcquireLatestFrame(); } catch { return; }
        using (frame)
        {
            if (frame?.VideoMediaFrame?.SoftwareBitmap is not SoftwareBitmap softwareBitmap)
                return;

            SoftwareBitmap? converted = null;
            try
            {
                // 统一转为 BGRA8
                if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    converted = SoftwareBitmap.Convert(softwareBitmap,
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                var target = converted ?? softwareBitmap;
                var width = target.PixelWidth;
                var height = target.PixelHeight;
                var pixelCount = (uint)(width * height * 4);
                var buffer = new global::Windows.Storage.Streams.Buffer(pixelCount);
                target.CopyToBuffer(buffer);

                using var dataReader = DataReader.FromBuffer(buffer);
                var bytes = new byte[buffer.Length];
                dataReader.ReadBytes(bytes);

                var skBitmap = new SKBitmap(width, height,
                    SKColorType.Bgra8888, SKAlphaType.Premul);

                unsafe
                {
                    fixed (byte* p = bytes)
                    {
                        global::System.Buffer.MemoryCopy(p,
                            skBitmap.GetPixels().ToPointer(),
                            bytes.Length, bytes.Length);
                    }
                }

                this.FrameReceived?.Invoke(this, skBitmap);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Frame error: {ex.Message}");
            }
            finally
            {
                converted?.Dispose();
            }
        }
    }

    // ========================================================================
    //  读取硬件能力
    // ========================================================================

    private void ReadCapabilities()
    {
        try
        {
            var zoom = this.mediaCapture?.VideoDeviceController?.ZoomControl;
            if (zoom?.Supported == true)
            {
                this.MinZoomFactor = (float)zoom.Min;
                this.MaxZoomFactor = (float)zoom.Max;
                this.CurrentZoomFactor = (float)zoom.Value;
            }
        }
        catch { }

        try
        {
            var exposure = this.mediaCapture?.VideoDeviceController?.ExposureControl;
            if (exposure?.Supported == true)
            {
                // ExposureControl.Min/Max/Step 是 TimeSpan（曝光时长），非 EV 值
                // 此处仅标记已启用，实际值由 SetExposureCompensationAsync 管理
            }
        }
        catch { }
    }

    // ========================================================================
    //  清理
    // ========================================================================

    public void Dispose()
    {
        StopPreviewAsync();
        this.frameReader?.Dispose();
        this.mediaCapture?.Dispose();
    }
}
#endif
