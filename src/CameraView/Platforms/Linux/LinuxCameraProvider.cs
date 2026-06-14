#if LINUX
using System.Runtime.InteropServices;
using CameraView.Models;
using CameraView.Services;
using SkiaSharp;

namespace CameraView.Platforms.Linux;

/// <summary>
/// Linux 相机提供者 — 基于 V4L2 + MJPEG 零转换帧管线
/// MJPEG 优先（SKBitmap.Decode 直解），YUYV 回退（unsafe 转换）
/// 不支持的功能静默忽略
/// </summary>
internal unsafe class LinuxCameraProvider : ICameraProvider
{
    // ========================================================================
    //  P/Invoke
    // ========================================================================

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, void* arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, void* buf, int count);

    private const int O_RDWR = 2;
    private const int O_NONBLOCK = 0o4000;

    // ========================================================================
    //  V4L2 常量和结构体
    // ========================================================================

    private const uint V4L2_BUF_TYPE_VIDEO_CAPTURE = 1;
    private const uint V4L2_FIELD_NONE = 0;
    private const uint V4L2_COLORSPACE_SRGB = 1;
    private const uint V4L2_FMT_FLAG_COMPRESSED = 0x0001;

    private const uint V4L2_CAP_VIDEO_CAPTURE = 0x00000001;
    private const uint V4L2_CAP_DEVICE_CAPS = 0x80000000;

    // FourCC
    private const uint V4L2_PIX_FMT_MJPEG = 0x47504A4D; // 'MJPG' LE
    private const uint V4L2_PIX_FMT_YUYV  = 0x56595559; // 'YUYV' LE

    /// <summary>V4L2 device capability</summary>
    private struct v4l2_capability
    {
        public fixed byte driver[16];
        public fixed byte card[32];
        public fixed byte bus_info[32];
        public uint version;
        public uint capabilities;
        public uint device_caps;
        public fixed uint reserved[3];
    }

    /// <summary>V4L2 pixel format</summary>
    private struct v4l2_pix_format
    {
        public uint width;
        public uint height;
        public uint pixelformat;
        public uint field;
        public uint bytesperline;
        public uint sizeimage;
        public uint colorspace;
        public uint priv;
        public uint flags;
        public uint ycbcr_enc;
        public uint quantizaion;
        public uint xfer_func;
    }

    /// <summary>V4L2 format descriptor</summary>
    private struct v4l2_fmtdesc
    {
        public uint index;
        public uint type;
        public uint flags;
        public fixed byte description[32];
        public uint pixelformat;
        public fixed uint reserved[4];
    }

    /// <summary>V4L2 format (type + union pix)</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct v4l2_format
    {
        [FieldOffset(0)]  public uint type;
        [FieldOffset(8)]  public v4l2_pix_format pix;
    }

    // ========================================================================
    //  ioctl 命令码计算
    // ========================================================================

    private static uint IOR(char type, byte nr, int size)
        => (2u << 30) | ((uint)type << 8) | nr | ((uint)size << 16);

    private static uint IOWR(char type, byte nr, int size)
        => (3u << 30) | ((uint)type << 8) | nr | ((uint)size << 16);

    private static uint IOW(char type, byte nr, int size)
        => (1u << 30) | ((uint)type << 8) | nr | ((uint)size << 16);

    private static readonly int SIZEOF_CAP       = sizeof(v4l2_capability);
    private static readonly int SIZEOF_FMT        = sizeof(v4l2_format);
    private static readonly int SIZEOF_FMTDESC    = sizeof(v4l2_fmtdesc);
    private static readonly int SIZEOF_INT        = sizeof(int);

    private static readonly uint VIDIOC_QUERYCAP = IOR('V', 0, SIZEOF_CAP);
    private static readonly uint VIDIOC_ENUM_FMT = IOR('V', 2, SIZEOF_FMTDESC);
    private static readonly uint VIDIOC_S_FMT    = IOWR('V', 5, SIZEOF_FMT);

    // ========================================================================
    //  成员变量
    // ========================================================================

    private int fd = -1;
    private int frameWidth;
    private int frameHeight;
    private uint frameFormat; // V4L2_PIX_FMT_MJPEG or V4L2_PIX_FMT_YUYV
    private bool started;
    private Thread? captureThread;
    private List<string>? devicePaths;
    private int currentDeviceIndex;

    public bool IsInitialized => this.fd >= 0;
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
    //  初始化 & 设备发现
    // ========================================================================

    public Task InitializeAsync(CameraOptions? options = null)
    {
        if (options?.CameraFacing == CameraFacing.Front)
            CurrentFacing = CameraFacing.Front;

        // Linux 上没有标准的前/后摄区分，遍历 /dev/video* 收集所有摄像头
        try
        {
            this.devicePaths = new List<string>();
            for (int i = 0; i < 64; i++)
            {
                var path = $"/dev/video{i}";
                int probe = open(path, O_RDWR | O_NONBLOCK);
                if (probe < 0) continue;

                var caps = new v4l2_capability();
                int ret;
                fixed (v4l2_capability* p = &caps)
                    ret = ioctl(probe, VIDIOC_QUERYCAP, p);

                bool isCapture = false;
                if (ret >= 0)
                {
                    uint caps_mask = (caps.capabilities & V4L2_CAP_DEVICE_CAPS) != 0
                        ? caps.device_caps : caps.capabilities;
                    isCapture = (caps_mask & V4L2_CAP_VIDEO_CAPTURE) != 0;
                }

                close(probe);
                if (isCapture)
                    this.devicePaths.Add(path);
            }

            if (this.devicePaths.Count == 0)
            {
                this.ErrorOccurred?.Invoke(this, "未找到 V4L2 摄像头设备。");
                return Task.CompletedTask;
            }

            this.currentDeviceIndex = 0;
            OpenDevice(this.currentDeviceIndex);
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"初始化失败: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>尝试设置指定的像素格式，返回是否成功</summary>
    private bool TrySetFormat(uint fourcc, int width = 640, int height = 480)
    {
        if (this.fd < 0) return false;

        var fmt = new v4l2_format
        {
            type = V4L2_BUF_TYPE_VIDEO_CAPTURE,
            pix = new v4l2_pix_format
            {
                width = (uint)width,
                height = (uint)height,
                pixelformat = fourcc,
                field = V4L2_FIELD_NONE,
                colorspace = V4L2_COLORSPACE_SRGB,
            }
        };

        int ret;
        fixed (v4l2_format* p = &fmt)
            ret = ioctl(this.fd, VIDIOC_S_FMT, p);

        if (ret < 0) return false;

        // 检查驱动是否接受了我们的格式
        this.frameWidth = (int)fmt.pix.width;
        this.frameHeight = (int)fmt.pix.height;
        this.frameFormat = fmt.pix.pixelformat;

        return fmt.pix.pixelformat == fourcc;
    }

    /// <summary>按索引打开 V4L2 设备并协商格式</summary>
    private void OpenDevice(int index)
    {
        if (this.devicePaths == null || index < 0 || index >= this.devicePaths.Count)
            return;

        if (this.fd >= 0)
            close(this.fd);

        this.currentDeviceIndex = index;
        this.fd = open(this.devicePaths[index], O_RDWR);

        if (!TrySetFormat(V4L2_PIX_FMT_MJPEG) && !TrySetFormat(V4L2_PIX_FMT_YUYV))
        {
            close(this.fd);
            this.fd = -1;
            this.ErrorOccurred?.Invoke(this, "摄像头不支持 MJPEG 或 YUYV 格式。");
        }
    }

    // ========================================================================
    //  预览启动/停止
    // ========================================================================

    public Task StartPreviewAsync()
    {
        if (this.fd < 0) return Task.CompletedTask;
        if (this.started) return Task.CompletedTask;

        this.started = true;
        this.captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "V4L2 Capture"
        };
        this.captureThread.Start();

        return Task.CompletedTask;
    }

    public Task StopPreviewAsync()
    {
        this.started = false;

        // close fd → 阻塞中的 read() 返回 0
        var tmp = Interlocked.Exchange(ref this.fd, -1);
        if (tmp >= 0)
            close(tmp);

        this.captureThread?.Join(500);
        this.captureThread = null;

        return Task.CompletedTask;
    }

    // ========================================================================
    //  帧捕获循环
    // ========================================================================

    private void CaptureLoop()
    {
        // 预分配 3MiB 缓冲区（足够 1080p MJPEG）
        int bufSize = 3 * 1024 * 1024;
        var buf = (byte*)NativeMemory.Alloc((nuint)bufSize);

        try
        {
            while (this.started && this.fd >= 0)
            {
                int n = read(this.fd, buf, bufSize);
                if (n <= 0) break;

                SKBitmap? skBitmap = null;
                if (this.frameFormat == V4L2_PIX_FMT_MJPEG)
                {
                    // MJPEG → SKBitmap.Decode 直解（零转换）
                    var span = new ReadOnlySpan<byte>(buf, n);
                    skBitmap = SKBitmap.Decode(span);
                }
                else if (this.frameFormat == V4L2_PIX_FMT_YUYV)
                {
                    // YUYV → BGRA（unsafe 逐像素转换，参考 Android 端代码）
                    skBitmap = ConvertYuyvToBgra(buf, n, this.frameWidth, this.frameHeight);
                }

                if (skBitmap != null)
                    this.FrameReceived?.Invoke(this, skBitmap);
                else
                    skBitmap?.Dispose();
            }
        }
        catch (Exception ex)
        {
            if (this.started)
                this.ErrorOccurred?.Invoke(this, $"捕获异常: {ex.Message}");
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    // ========================================================================
    //  YUYV → BGRA（回退路径）
    // ========================================================================

    private static SKBitmap? ConvertYuyvToBgra(byte* yuyv, int length, int width, int height)
    {
        int expectedSize = width * height * 2; // YUYV: 2 bytes/pixel
        if (length < expectedSize) return null;

        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var dst = (byte*)bmp.GetPixels().ToPointer();

        for (int i = 0; i < width * height / 2; i++)
        {
            int y0 = yuyv[i * 4 + 0];
            int u  = yuyv[i * 4 + 1] - 128;
            int y1 = yuyv[i * 4 + 2];
            int v  = yuyv[i * 4 + 3] - 128;

            // Y0
            dst[i * 8 + 0] = ClampByte(y0 + ((516 * v + 128) >> 8));          // B
            dst[i * 8 + 1] = ClampByte(y0 - ((100 * u + 208 * v + 128) >> 8)); // G
            dst[i * 8 + 2] = ClampByte(y0 + ((269 * u + 128) >> 8));           // R
            dst[i * 8 + 3] = 255;

            // Y1
            dst[i * 8 + 4] = ClampByte(y1 + ((516 * v + 128) >> 8));
            dst[i * 8 + 5] = ClampByte(y1 - ((100 * u + 208 * v + 128) >> 8));
            dst[i * 8 + 6] = ClampByte(y1 + ((269 * u + 128) >> 8));
            dst[i * 8 + 7] = 255;
        }

        return bmp;
    }

    private static byte ClampByte(int v)
        => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // ========================================================================
    //  拍照（MJPEG 模式下直接取当前帧，YUYV 模式需编码）
    // ========================================================================

    public Task TakePhotoAsync()
    {
        // 拍照与预览帧使用相同数据源
        // 对用户：绑定 PhotoCapturedCommand 或监听 PhotoCaptured 事件
        // 由于 read() 循环在后台线程持续运行，这里不额外捕获
        // 可在 UI 层通过预览帧中转 JPEG 编码
        return Task.CompletedTask;
    }

    // ========================================================================
    //  切换摄像头（遍历 /dev/video* 找下一个设备）
    // ========================================================================

    public async Task SwitchCameraAsync(CameraFacing facing)
    {
        // 桌面端多摄像头：循环切换（忽略 facing）
        if (this.devicePaths == null || this.devicePaths.Count <= 1)
            return;

        var wasRunning = this.started;
        await StopPreviewAsync();
        this.frameFormat = 0;

        var nextIndex = (this.currentDeviceIndex + 1) % this.devicePaths.Count;
        OpenDevice(nextIndex);

        if (wasRunning && this.fd >= 0)
            await StartPreviewAsync();
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
        this.PhotoResolution = resolution;
        return Task.CompletedTask;
    }

    public Task SetExposureCompensationAsync(float ev)
    {
        this.ExposureCompensation = ev;
        return Task.CompletedTask;
    }

    // ========================================================================
    //  清理
    // ========================================================================

    public void Dispose()
    {
        StopPreviewAsync();
    }
}
#endif
