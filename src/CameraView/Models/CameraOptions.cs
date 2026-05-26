namespace CameraView.Models;

public enum CaptureQuality
{
    Low,
    Medium,
    High,
    Highest
}

public record CameraOptions
{
    public CameraFacing CameraFacing { get; init; } = CameraFacing.Back;
    public CaptureQuality CaptureQuality { get; init; } = CaptureQuality.Medium;
    public bool TorchOn { get; init; } = false;
    public float? RequestZoomFactor { get; init; }
    public uint? FrameRate { get; init; }
}
