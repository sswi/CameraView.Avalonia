namespace CameraView.Models;

/// <summary>
/// 图像分析画质
/// </summary>
public enum CaptureQuality
{
    Low,
    Medium,
    High,
    Highest
}

/// <summary>
/// 相机初始化配置
/// </summary>
public record CameraOptions
{
    /// <summary>前后摄像头</summary>
    public CameraFacing CameraFacing { get; init; } = CameraFacing.Back;
    /// <summary>画质</summary>
    public CaptureQuality CaptureQuality { get; init; } = CaptureQuality.Medium;
    /// <summary>手电筒</summary>
    public bool TorchOn { get; init; } = false;
    /// <summary>请求缩放倍率</summary>
    public float? RequestZoomFactor { get; init; }
    /// <summary>帧率限制</summary>
    public uint? FrameRate { get; init; }
}
