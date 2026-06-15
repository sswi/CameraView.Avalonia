using AVFoundation;
using CoreMedia;
using CoreVideo;
using SkiaSharp;
using UIKit;

namespace CameraView.Platforms.iOS;

internal class FrameAnalyzer : AVCaptureVideoDataOutputSampleBufferDelegate
{
    private readonly Action<SKBitmap> onFrameReceived;
    private readonly Action<int>? onRotationChanged;
    private uint? skippedFrames;
    private uint? frameRate;
    private bool disposed;
    private int _rotationAngle = 90;

    public FrameAnalyzer(Action<SKBitmap> onFrameReceived, Action<int>? onRotationChanged = null)
    {
        this.onFrameReceived = onFrameReceived;
        this.onRotationChanged = onRotationChanged;
        this.onRotationChanged?.Invoke(90); // 初始值
    }

    public uint? FrameRate
    {
        get => this.frameRate;
        set
        {
            this.frameRate = value;
            this.skippedFrames = null;
        }
    }

    /// <summary>根据设备方向设置帧旋转角度（由 iOSCameraProvider 调用）</summary>
    public void SetDeviceOrientation(UIDeviceOrientation orientation)
    {
        if (orientation == UIDeviceOrientation.Unknown)
        {
            orientation = UIApplication.SharedApplication.StatusBarOrientation switch
            {
                UIInterfaceOrientation.LandscapeLeft => UIDeviceOrientation.LandscapeRight,
                UIInterfaceOrientation.LandscapeRight => UIDeviceOrientation.LandscapeLeft,
                _ => UIDeviceOrientation.Portrait,
            };
        }

        // 合并两次旋转：预览基准 + 照片额外纠正
        // 朝左: 0 + 270 = 270（逆时针90°）
        // 朝右: 180 + 0 = 180（不变）
        // 倒置: 180 + 180 = 360 = 0
        // 竖屏: 90 + 0 = 90
        _rotationAngle = orientation switch
        {
            UIDeviceOrientation.Portrait => 90,
            UIDeviceOrientation.LandscapeLeft => 270,
            UIDeviceOrientation.LandscapeRight => 180,
            UIDeviceOrientation.PortraitUpsideDown => 0,
            _ => 90,
        };
        this.onRotationChanged?.Invoke(_rotationAngle);
    }

    public override void DidOutputSampleBuffer(
        AVCaptureOutput captureOutput,
        CMSampleBuffer sampleBuffer,
        AVCaptureConnection connection)
    {
        try
        {
            if (this.disposed) return;

            if (this.FrameRate is uint r && r > 1)
            {
                if (this.skippedFrames != null && ++this.skippedFrames < r)
                    return;
                this.skippedFrames = 0;
            }

            using var imageBuffer = sampleBuffer.GetImageBuffer();
            if (imageBuffer is not CVPixelBuffer pixelBuffer) return;

            pixelBuffer.Lock(CVPixelBufferLock.ReadOnly);
            try
            {
                if (pixelBuffer.PixelFormatType == CVPixelFormatType.CV32BGRA)
                    CopyBGRA(pixelBuffer);
                else
                    CopyViaCIImage(pixelBuffer);
            }
            finally
            {
                pixelBuffer.Unlock(CVPixelBufferLock.ReadOnly);
            }
        }
        catch { }
        finally
        {
            try { sampleBuffer.Dispose(); } catch { }
        }
    }

    private unsafe void CopyBGRA(CVPixelBuffer pixelBuffer)
    {
        int w = (int)pixelBuffer.Width;
        int h = (int)pixelBuffer.Height;
        int bpr = (int)pixelBuffer.BytesPerRow;
        var baseAddr = pixelBuffer.BaseAddress;
        if (baseAddr == IntPtr.Zero) return;

        int angle = _rotationAngle;
        bool needRotate = angle == 90 || angle == 270;
        int dw = needRotate ? h : w;
        int dh = needRotate ? w : h;

        var skBitmap = new SKBitmap(dw, dh, SKColorType.Bgra8888, SKAlphaType.Premul);

        if (angle == 0)
        {
            System.Buffer.MemoryCopy(
                baseAddr.ToPointer(), skBitmap.GetPixels().ToPointer(), dh * bpr, dh * bpr);
        }
        else
        {
            using var temp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            System.Buffer.MemoryCopy(
                baseAddr.ToPointer(), temp.GetPixels().ToPointer(), h * bpr, h * bpr);

            using var canvas = new SKCanvas(skBitmap);
            canvas.Translate(dw / 2f, dh / 2f);
            canvas.RotateDegrees(angle);
            canvas.Translate(-w / 2f, -h / 2f);
            canvas.DrawBitmap(temp, 0, 0);
        }

        this.onFrameReceived(skBitmap);
    }

    private void CopyViaCIImage(CVPixelBuffer pixelBuffer)
    {
        using var ciImage = new CoreImage.CIImage(pixelBuffer);
        using var context = new CoreImage.CIContext();
        using var cgImage = context.CreateCGImage(ciImage, ciImage.Extent);
        if (cgImage != null)
        {
            using var skBitmap = CGImageToSKBitmap(cgImage);
            if (skBitmap != null)
                this.onFrameReceived(skBitmap);
        }
    }

    private static SKBitmap? CGImageToSKBitmap(CoreGraphics.CGImage cgImage)
    {
        int w = (int)cgImage.Width, h = (int)cgImage.Height;
        int bpr = (int)cgImage.BytesPerRow;
        var data = new byte[h * bpr];
        using var cs = CoreGraphics.CGColorSpace.CreateDeviceRGB();
        using var ctx = new CoreGraphics.CGBitmapContext(
            data, w, h, 8, bpr, cs,
            CoreGraphics.CGBitmapFlags.PremultipliedFirst | CoreGraphics.CGBitmapFlags.ByteOrder32Little);
        ctx.DrawImage(new CoreGraphics.CGRect(0, 0, w, h), cgImage);
        var sk = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe { fixed (byte* p = data) { System.Buffer.MemoryCopy(p, sk.GetPixels().ToPointer(), data.Length, data.Length); } }
        return sk;
    }

    protected override void Dispose(bool disposing)
    {
        this.disposed = true;
        base.Dispose(disposing);
    }
}
