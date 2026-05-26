namespace CameraView.Models;

/// <summary>
/// 设备朝向状态（人可读）
/// </summary>
public enum DeviceOrientationState
{
    /// <summary>顶朝上（正常竖持）</summary>
    PortraitUpright,
    /// <summary>顶朝下（倒立）</summary>
    PortraitUpsideDown,
    /// <summary>朝左（横屏左侧朝下）</summary>
    LandscapeLeft,
    /// <summary>朝右（横屏右侧朝下）</summary>
    LandscapeRight,
    /// <summary>平放（屏幕朝上）</summary>
    FlatFaceUp,
    /// <summary>倒置（屏幕朝地面）</summary>
    FlatFaceDown
}
