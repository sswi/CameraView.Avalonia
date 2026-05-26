namespace CameraView.Models;

public class PhotoResult
{
    public byte[] ImageData { get; init; } = Array.Empty<byte>();
    public int Width { get; init; }
    public int Height { get; init; }
    public string? FilePath { get; init; }
}
