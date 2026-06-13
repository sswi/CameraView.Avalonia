using AVFoundation;
using CoreMedia;
using CoreVideo;
using SkiaSharp;

namespace CameraView.Platforms.iOS;

internal class FrameAnalyzer : AVCaptureVideoDataOutputSampleBufferDelegate
{
    private readonly Action<SKBitmap> onFrameReceived;
    private uint? skippedFrames;
    private uint? frameRate;
    private bool disposed;

    public FrameAnalyzer(Action<SKBitmap> onFrameReceived)
    {
        this.onFrameReceived = onFrameReceived;
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
                {
                    return;
                }
                this.skippedFrames = 0;
            }

            using var imageBuffer = sampleBuffer.GetImageBuffer();
            if (imageBuffer is not CVPixelBuffer pixelBuffer) return;

            pixelBuffer.Lock(CVPixelBufferLock.ReadOnly);
            try
            {
                var pixelFormat = pixelBuffer.PixelFormatType;

                if (pixelFormat == CVPixelFormatType.CV32BGRA)
                {
                    // 直接内存拷贝（BGRA → SKBitmap），跳过 CIImage/CGImage 中间层
                    CopyBGRAFromPixelBuffer(pixelBuffer);
                }
                else
                {
                    // 回退：通过 CIImage 转换（兼容非 BGRA 输出格式）
                    CopyViaCIImage(pixelBuffer);
                }
            }
            finally
            {
                pixelBuffer.Unlock(CVPixelBufferLock.ReadOnly);
            }
        }
        catch
        {
            // Frame processing failed, continue
        }
        finally
        {
            try { sampleBuffer.Dispose(); } catch { }
        }
    }

    private unsafe void CopyBGRAFromPixelBuffer(CVPixelBuffer pixelBuffer)
    {
        int width = (int)pixelBuffer.Width;
        int height = (int)pixelBuffer.Height;
        int bytesPerRow = (int)pixelBuffer.BytesPerRow;
        var baseAddress = pixelBuffer.BaseAddress;

        if (baseAddress == IntPtr.Zero) return;

        var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Buffer.MemoryCopy(
            baseAddress.ToPointer(),
            skBitmap.GetPixels().ToPointer(),
            height * bytesPerRow,
            height * bytesPerRow);

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
            {
                this.onFrameReceived(skBitmap);
            }
        }
    }

    private static SKBitmap? CGImageToSKBitmap(CoreGraphics.CGImage cgImage)
    {
        var width = (int)cgImage.Width;
        var height = (int)cgImage.Height;
        var bytesPerRow = (int)cgImage.BytesPerRow;
        var data = new byte[height * bytesPerRow];

        using var colorSpace = CoreGraphics.CGColorSpace.CreateDeviceRGB();
        using var context = new CoreGraphics.CGBitmapContext(
            data, width, height, 8, bytesPerRow,
            colorSpace, CoreGraphics.CGBitmapFlags.PremultipliedFirst | CoreGraphics.CGBitmapFlags.ByteOrder32Little);

        context.DrawImage(new CoreGraphics.CGRect(0, 0, width, height), cgImage);

        var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* src = data)
            {
                Buffer.MemoryCopy(src, skBitmap.GetPixels().ToPointer(), data.Length, data.Length);
            }
        }

        return skBitmap;
    }

    protected override void Dispose(bool disposing)
    {
        this.disposed = true;
        base.Dispose(disposing);
    }
}
