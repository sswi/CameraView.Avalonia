using System;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Runtime;
using AndroidX.Camera.Core;
using SkiaSharp;

namespace CameraView.Platforms.Android;

[Register("cameraview/platforms/android/FrameAnalyzer")]
public class FrameAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
{
    private readonly Action<SKBitmap> onFrameReceived;
    private uint frameCount;

    public FrameAnalyzer(Action<SKBitmap> onFrameReceived)
    {
        this.onFrameReceived = onFrameReceived;
    }

    public global::Android.Util.Size DefaultTargetResolution => new(720, 1280);

    public int TargetCoordinateSystem => ImageAnalysis.CoordinateSystemOriginal;

    public void Analyze(IImageProxy proxy)
    {
        try
        {
            // Process every frame for smooth preview
            var image = proxy.Image;
            if (image == null) return;

            int rotationDegrees = proxy.ImageInfo?.RotationDegrees ?? 0;

            var yuvPlanes = image.GetPlanes();
            if (yuvPlanes == null || yuvPlanes.Length < 3) return;

            var yPlane = yuvPlanes[0];
            var uPlane = yuvPlanes[1];
            var vPlane = yuvPlanes[2];

            int width = image.Width;
            int height = image.Height;
            int yRowStride = yPlane.RowStride;
            int uvRowStride = uPlane.RowStride;
            int uvPixelStride = uPlane.PixelStride;

            var yBuffer = yPlane.Buffer;
            var uBuffer = uPlane.Buffer;
            var vBuffer = vPlane.Buffer;

            var yData = new byte[yBuffer.Remaining()];
            var uData = new byte[uBuffer.Remaining()];
            var vData = new byte[vBuffer.Remaining()];

            yBuffer.Get(yData); uBuffer.Get(uData); vBuffer.Get(vData);
            yBuffer.Rewind(); uBuffer.Rewind(); vBuffer.Rewind();

            Task.Run(() =>
            {
                try
                {
                    var skBitmap = YuvToSkBitmap(
                        yData, uData, vData,
                        width, height,
                        yRowStride, uvRowStride, uvPixelStride);

                    if (skBitmap != null)
                    {
                        if (rotationDegrees != 0)
                        {
                            var rotated = RotateBitmap(skBitmap, rotationDegrees);
                            skBitmap.Dispose();
                            skBitmap = rotated;
                        }

                        if (skBitmap != null)
                        {
                            this.onFrameReceived(skBitmap);
                            skBitmap.Dispose();
                        }
                    }
                }
                catch { }
            });
        }
        catch { }
        finally
        {
            try { proxy.Close(); } catch { }
        }
    }

    private static unsafe SKBitmap? YuvToSkBitmap(
        byte[] yData, byte[] uData, byte[] vData,
        int width, int height,
        int yRowStride, int uvRowStride, int uvPixelStride)
    {
        var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var dstPtr = (byte*)skBitmap.GetPixels().ToPointer();

        fixed (byte* pY = yData, pU = uData, pV = vData)
        {
            for (int row = 0; row < height; row++)
            {
                int yRowOff = row * yRowStride;
                int uvRowOff = (row / 2) * uvRowStride;
                int dstRowOff = row * width * 4;

                for (int col = 0; col < width; col++)
                {
                    int y = pY[yRowOff + col] & 0xFF;
                    int u = pU[uvRowOff + (col / 2) * uvPixelStride] & 0xFF;
                    int v = pV[uvRowOff + (col / 2) * uvPixelStride] & 0xFF;

                    int c = y - 16;
                    int d = u - 128;
                    int e = v - 128;

                    int r = Clamp((298 * c + 409 * e + 128) >> 8);
                    int g = Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                    int b = Clamp((298 * c + 516 * d + 128) >> 8);

                    int di = dstRowOff + col * 4;
                    dstPtr[di + 0] = (byte)b;
                    dstPtr[di + 1] = (byte)g;
                    dstPtr[di + 2] = (byte)r;
                    dstPtr[di + 3] = 255;
                }
            }
        }

        return skBitmap;
    }

    private static int Clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

    private static SKBitmap? RotateBitmap(SKBitmap bitmap, int degrees)
    {
        degrees %= 360;
        if (degrees == 0) return null;

        int newW = (degrees == 90 || degrees == 270) ? bitmap.Height : bitmap.Width;
        int newH = (degrees == 90 || degrees == 270) ? bitmap.Width : bitmap.Height;

        var rotated = new SKBitmap(newW, newH, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(newW / 2f, newH / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
        canvas.DrawBitmap(bitmap, 0, 0);
        return rotated;
    }
}
