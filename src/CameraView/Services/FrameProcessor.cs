using System.Diagnostics;

namespace CameraView.Services;

/// <summary>
/// 帧处理器 — 将相机原始帧缩放、编码为 Avalonia Bitmap，并统计 FPS
/// </summary>
public class FrameProcessor
{
    /// <summary>帧就绪事件（UI 线程回调）</summary>
    public event Action<Bitmap>? FrameReady;
    /// <summary>FPS 更新事件</summary>
    public event Action<string>? FpsUpdated;

    private Bitmap? currentBitmap;
    private readonly Stopwatch fpsStopwatch = Stopwatch.StartNew();
    private int frameCount;
    private string fpsText = "";

    public Bitmap? ProcessedBitmap => currentBitmap;
    public string FpsText => fpsText;

    /// <summary>
    /// 处理预览帧：缩放 → JPEG 编码 → Avalonia Bitmap（后台线程执行重活，UI 线程只做赋值）
    /// </summary>
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

            using var skImage = SKImage.FromBitmap(resized);
            using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 85);
            var jpegBytes = encoded.ToArray();

            using var stream = new MemoryStream(jpegBytes);
            var bmp = new Bitmap(stream);

            // FPS 统计
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

            // UI 线程：仅赋值和触发事件
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var old = currentBitmap;
                    currentBitmap = bmp;
                    old?.Dispose();
                    FrameReady?.Invoke(bmp);
                }
                catch { }
            });
        }
        catch { }
    }
}
