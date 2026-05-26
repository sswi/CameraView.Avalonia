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
            int maxDim = Math.Max(rawFrame.Width, rawFrame.Height);
            float scale = Math.Min(1f, 960f / maxDim);
            int newW = Math.Max(1, (int)(rawFrame.Width * scale));
            int newH = Math.Max(1, (int)(rawFrame.Height * scale));

            using var resized = rawFrame.Resize(
                new SKImageInfo(newW, newH, SKColorType.Bgra8888),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            var wb = new WriteableBitmap(
                new PixelSize(newW, newH),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);

            using var fb = wb.Lock();
            var src = resized.GetPixels();
            var srcRowBytes = resized.RowBytes;
            var dstRowBytes = fb.RowBytes;
            var copyBytes = Math.Min(srcRowBytes, dstRowBytes);

            unsafe
            {
                var s = (byte*)src.ToPointer();
                var d = (byte*)fb.Address.ToPointer();
                for (int row = 0; row < newH; row++)
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
                fpsText = $"{fps:F0} FPS | {newW}x{newH}";
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
