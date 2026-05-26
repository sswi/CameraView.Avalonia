using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace CameraView.Services;

public class FrameProcessor
{
    public event Action<Avalonia.Media.Imaging.Bitmap>? FrameReady;
    public event Action<string>? FpsUpdated;

    private Avalonia.Media.Imaging.Bitmap? currentBitmap;
    private readonly Stopwatch fpsStopwatch = Stopwatch.StartNew();
    private int frameCount;
    private string fpsText = "";

    public Avalonia.Media.Imaging.Bitmap? ProcessedBitmap => this.currentBitmap;
    public string FpsText => this.fpsText;

    public void ProcessPreviewFrame(SKBitmap rawFrame, int previewWidth, int previewHeight)
    {
        try
        {
            int maxDim = Math.Max(rawFrame.Width, rawFrame.Height);
            float scale = Math.Min(1f, 960f / maxDim);

            int newW = Math.Max(1, (int)(rawFrame.Width * scale));
            int newH = Math.Max(1, (int)(rawFrame.Height * scale));

            using var resized = rawFrame.Resize(
                new SKImageInfo(newW, newH, SKColorType.Bgra8888),
                SKFilterQuality.Medium);

            using var skImage = SKImage.FromBitmap(resized);
            using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 85);
            var jpegBytes = encoded.ToArray();

            this.frameCount++;
            if (this.fpsStopwatch.Elapsed.TotalSeconds >= 1.0)
            {
                double fps = this.frameCount / this.fpsStopwatch.Elapsed.TotalSeconds;
                this.fpsText = $"{fps:F0} FPS | {newW}x{newH}";
                this.frameCount = 0;
                this.fpsStopwatch.Restart();
                var f = this.fpsText;
                Dispatcher.UIThread.Post(() => this.FpsUpdated?.Invoke(f));
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var stream = new MemoryStream(jpegBytes);
                    var bmp = new Avalonia.Media.Imaging.Bitmap(stream);
                    var old = this.currentBitmap;
                    this.currentBitmap = bmp;
                    old?.Dispose();
                    this.FrameReady?.Invoke(bmp);
                }
                catch { }
            });
        }
        catch { }
    }
}
