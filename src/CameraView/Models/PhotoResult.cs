namespace CameraView.Models;

/// <summary>
/// 照片数据封装
/// </summary>
public class PhotoResult
{
    /// <summary>JPEG 字节</summary>
    public byte[] ImageData { get; init; } = Array.Empty<byte>();
    /// <summary>宽度</summary>
    public int Width { get; init; }
    /// <summary>高度</summary>
    public int Height { get; init; }
    /// <summary>保存路径</summary>
    public string? FilePath { get; init; }
}
