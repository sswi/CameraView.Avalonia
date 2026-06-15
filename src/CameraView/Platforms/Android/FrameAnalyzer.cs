using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AndroidX.Camera.Core;

namespace CameraView.Platforms.Android;

[Register("cameraview/platforms/android/FrameAnalyzer")]
public class FrameAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
{
    private readonly Action<SKBitmap> onFrameReceived;

    public FrameAnalyzer(Action<SKBitmap> onFrameReceived)
    {
        this.onFrameReceived = onFrameReceived;
    }

    public global::Android.Util.Size DefaultTargetResolution => new(720, 1280);

    public int TargetCoordinateSystem => ImageAnalysis.CoordinateSystemOriginal;

    public void Analyze(IImageProxy? proxy)
    {
        try
        {
            // Process every frame for smooth preview
            var image = proxy?.Image;
            if (image == null) return;

            int rotationDegrees = proxy?.ImageInfo?.RotationDegrees ?? 0;

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

            int yLen = yBuffer.Remaining();
            int uLen = uBuffer.Remaining();
            int vLen = vBuffer.Remaining();

            var yData = ArrayPool<byte>.Shared.Rent(yLen);
            var uData = ArrayPool<byte>.Shared.Rent(uLen);
            var vData = ArrayPool<byte>.Shared.Rent(vLen);

            yBuffer.Get(yData, 0, yLen); uBuffer.Get(uData, 0, uLen); vBuffer.Get(vData, 0, vLen);
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
                finally
                {
                    ArrayPool<byte>.Shared.Return(yData);
                    ArrayPool<byte>.Shared.Return(uData);
                    ArrayPool<byte>.Shared.Return(vData);
                }
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

        fixed (byte* pY = yData, pU = uData, pV = vData)
        {
            var srcY = pY;
            var srcU = pU;
            var srcV = pV;
            var dst = (byte*)skBitmap.GetPixels().ToPointer();
            int dstRowBytes = width * 4;

            Parallel.For(0, height, row =>
            {
                int yRowOff = row * yRowStride;
                int uvRowOff = (row / 2) * uvRowStride;
                int dstRowOff = row * dstRowBytes;

                for (int col = 0; col < width - 1; col += 2)
                {
                    int uvIdx = uvRowOff + (col / 2) * uvPixelStride;
                    int u = srcU[uvIdx] & 0xFF;
                    int v = srcV[uvIdx] & 0xFF;
                    int d = u - 128;
                    int e = v - 128;

                    int y0 = srcY[yRowOff + col] & 0xFF;
                    YuvToBgra(y0, d, e, dst + dstRowOff + col * 4);

                    int y1 = srcY[yRowOff + col + 1] & 0xFF;
                    YuvToBgra(y1, d, e, dst + dstRowOff + (col + 1) * 4);
                }

                if ((width & 1) != 0)
                {
                    int col = width - 1;
                    int uvIdx = uvRowOff + (col / 2) * uvPixelStride;
                    int y0 = srcY[yRowOff + col] & 0xFF;
                    YuvToBgra(y0, srcU[uvIdx] - 128, srcV[uvIdx] - 128, dst + dstRowOff + col * 4);
                }
            });
        }

        return skBitmap;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void YuvToBgra(int y, int d, int e, byte* dst)
    {
        int c = y - 16;
        dst[0] = (byte)Clamp((298 * c + 516 * d + 128) >> 8);   // B
        dst[1] = (byte)Clamp((298 * c - 100 * d - 208 * e + 128) >> 8); // G
        dst[2] = (byte)Clamp((298 * c + 409 * e + 128) >> 8);   // R
        dst[3] = 255;                                             // A
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
