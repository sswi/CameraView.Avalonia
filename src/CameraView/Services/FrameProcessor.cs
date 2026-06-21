using System.Diagnostics;

namespace CameraView.Services;

/// <summary>
/// 帧处理器 — 缩放 + 直拷像素到 WriteableBitmap，零编码开销
/// </summary>
public class FrameProcessor
{
    public event Action<Bitmap>? FrameReady;
    public event Action<string>? FpsUpdated;

    private readonly Stopwatch fpsStopwatch = Stopwatch.StartNew();
    private int frameCount;
    private string fpsText = "";

    public string FpsText => fpsText;

    public void ProcessPreviewFrame(SKBitmap rawFrame)
    {
        try
        {
            int w = rawFrame.Width;
            int h = rawFrame.Height;

            var wb = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);

            using var fb = wb.Lock();
            var src = rawFrame.GetPixels();
            var srcRowBytes = rawFrame.RowBytes;
            var dstRowBytes = fb.RowBytes;
            var copyBytes = Math.Min(srcRowBytes, dstRowBytes);

            unsafe
            {
                var s = (byte*)src.ToPointer();
                var d = (byte*)fb.Address.ToPointer();
                int rows = Math.Min(h, fb.Size.Height);
                for (int row = 0; row < rows; row++)
                {
                    Buffer.MemoryCopy(s, d, copyBytes, copyBytes);
                    s += srcRowBytes;
                    d += dstRowBytes;
                }
            }

            // FPS
            frameCount++;
            if (fpsStopwatch.Elapsed.TotalSeconds >= 1.0)
            {
                double fps = frameCount / fpsStopwatch.Elapsed.TotalSeconds;
                fpsText = $"{fps:F0} FPS | {w}x{h}";
                frameCount = 0;
                fpsStopwatch.Restart();
                var f = fpsText;
                Dispatcher.UIThread.Post(() => FpsUpdated?.Invoke(f));
            }

            Dispatcher.UIThread.Post(() =>
            {
                try { FrameReady?.Invoke(wb); }
                catch { }
            });
        }
        catch { }
    }
}
