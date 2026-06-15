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
    private sealed record PhotoEncodingOption(PhotoResolution Resolution, ImageEncodingProperties Properties);

    private MediaCapture? mediaCapture;
    private MediaFrameReader? frameReader;
    private bool started;
    private List<DeviceInformation>? cameraDevices;
    private int currentDeviceIndex;
    private List<PhotoEncodingOption> supportedPhotoEncodingOptions = [];

    public bool IsInitialized => this.mediaCapture != null;
    public CameraFacing CurrentFacing { get; private set; } = CameraFacing.Back;
    public float? MinZoomFactor { get; private set; }
    public float? MaxZoomFactor { get; private set; }
    public float? CurrentZoomFactor { get; private set; }
    public FlashMode FlashMode { get; private set; } = FlashMode.Auto;
    public PhotoResolution PhotoResolution { get; private set; } = PhotoResolution.DefaultPresets[0];
    public IReadOnlyList<PhotoResolution> SupportedPhotoResolutions => this.supportedPhotoEncodingOptions.Count > 0
        ? this.supportedPhotoEncodingOptions.Select(x => x.Resolution).ToList()
        : PhotoResolution.DefaultPresets;
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

        try
        {
            this.mediaCapture?.Dispose();
            this.mediaCapture = null;

            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = this.cameraDevices[index].Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            };

            this.mediaCapture = new MediaCapture();
            await this.mediaCapture.InitializeAsync(settings);

            this.currentDeviceIndex = index;
            CurrentFacing = this.cameraDevices[index].EnclosureLocation?.Panel
                == global::Windows.Devices.Enumeration.Panel.Front
                ? CameraFacing.Front : CameraFacing.Back;

            ReadCapabilities();
            this.UpdateSupportedPhotoResolutions();
            this.PhotoResolution = this.GetBestSupportedPhotoResolution(this.PhotoResolution);
            await this.ApplySelectedPhotoResolutionAsync();
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this,
                $"摄像头 #{index} ({this.cameraDevices[index].Name}) 初始化失败: {ex.Message}");
        }
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
            System.Diagnostics.Debug.WriteLine(
                $"CameraView: preview started — {source.Info.DeviceInformation?.Name}, " +
                $"streamType={source.Info.MediaStreamType}, " +
                $"sourceKind={source.Info.SourceKind}");
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
            var encodingProperties = this.GetSelectedPhotoEncodingProperties() ?? ImageEncodingProperties.CreateJpeg();
            await this.mediaCapture.CapturePhotoToStreamAsync(
                encodingProperties, stream);

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
        this.UpdateSupportedPhotoResolutions();
        this.PhotoResolution = this.GetBestSupportedPhotoResolution(resolution);
        return this.ApplySelectedPhotoResolutionAsync();
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
        if (frame?.VideoMediaFrame == null) return;

        // 后台异步处理帧（需处理 DX surface → SoftwareBitmap 转换）
        _ = ProcessFrameAsync(frame);
    }

    private async Task ProcessFrameAsync(MediaFrameReference frame)
    {
        using (frame)
        {
            SoftwareBitmap? softwareBitmap = frame.VideoMediaFrame?.SoftwareBitmap;

            if (softwareBitmap == null && frame.VideoMediaFrame?.Direct3DSurface != null)
            {
                try
                {
                    softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                        frame.VideoMediaFrame.Direct3DSurface);
                    System.Diagnostics.Debug.WriteLine(
                        $"CameraView: frame from DX surface, format={softwareBitmap.BitmapPixelFormat}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"CameraView: DX surface convert error: {ex.Message}");
                    return;
                }
            }

            if (softwareBitmap == null)
            {
                System.Diagnostics.Debug.WriteLine("CameraView: frame has no SoftwareBitmap or Direct3DSurface");
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                $"CameraView: frame arrived — format={softwareBitmap.BitmapPixelFormat}, " +
                $"{softwareBitmap.PixelWidth}x{softwareBitmap.PixelHeight}, " +
                $"hasSW={frame.VideoMediaFrame?.SoftwareBitmap != null}, " +
                $"hasDX={frame.VideoMediaFrame?.Direct3DSurface != null}");

            using (softwareBitmap)
            {
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
                    System.Diagnostics.Debug.WriteLine($"Frame process error: {ex.Message}");
                }
                finally
                {
                    converted?.Dispose();
                }
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

    private void UpdateSupportedPhotoResolutions()
    {
        try
        {
            var options = this.mediaCapture?.VideoDeviceController?
                .GetAvailableMediaStreamProperties(MediaStreamType.Photo)
                .OfType<ImageEncodingProperties>()
                .Where(p => p.Width > 0 && p.Height > 0)
                .GroupBy(p => (p.Width, p.Height))
                .Select(g => new PhotoEncodingOption(
                    new PhotoResolution((int)g.Key.Width, (int)g.Key.Height,
                        CreatePhotoResolutionLabel((int)g.Key.Width, (int)g.Key.Height)),
                    g.First()))
                .OrderByDescending(x => x.Resolution.Width * x.Resolution.Height)
                .ThenByDescending(x => x.Resolution.Width)
                .ToList();

            this.supportedPhotoEncodingOptions = options is { Count: > 0 }
                ? options
                : [];
        }
        catch
        {
            this.supportedPhotoEncodingOptions = [];
        }
    }

    private PhotoResolution GetBestSupportedPhotoResolution(PhotoResolution preferred)
    {
        if (this.supportedPhotoEncodingOptions.Count == 0)
            return preferred;

        var resolutions = this.supportedPhotoEncodingOptions.Select(x => x.Resolution).ToList();
        var exact = resolutions.FirstOrDefault(r => r.Width == preferred.Width && r.Height == preferred.Height);
        if (exact != null)
            return exact;

        var preferredPixels = preferred.Width * preferred.Height;
        return resolutions
            .OrderBy(resolution => Math.Abs((resolution.Width * resolution.Height) - preferredPixels))
            .ThenBy(resolution => Math.Abs(((double)resolution.Width / resolution.Height) - preferred.AspectRatio))
            .First();
    }

    private ImageEncodingProperties? GetSelectedPhotoEncodingProperties()
    {
        if (this.supportedPhotoEncodingOptions.Count == 0)
            return null;

        var exact = this.supportedPhotoEncodingOptions.FirstOrDefault(x =>
            x.Resolution.Width == this.PhotoResolution.Width && x.Resolution.Height == this.PhotoResolution.Height);
        if (exact != null)
            return exact.Properties;

        var best = this.GetBestSupportedPhotoResolution(this.PhotoResolution);
        return this.supportedPhotoEncodingOptions
            .FirstOrDefault(x => x.Resolution.Width == best.Width && x.Resolution.Height == best.Height)
            ?.Properties;
    }

    private async Task ApplySelectedPhotoResolutionAsync()
    {
        if (this.mediaCapture == null)
            return;

        var properties = this.GetSelectedPhotoEncodingProperties();
        if (properties == null)
            return;

        try
        {
            await this.mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.Photo, properties, null);
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"设置拍照分辨率失败: {ex.Message}");
        }
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
