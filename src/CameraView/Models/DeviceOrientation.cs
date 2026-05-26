namespace CameraView.Models;

/// <summary>
/// 设备朝向数据（来自 IMU 传感器）
/// </summary>
/// <param name="Pitch">俯仰角 °</param>
/// <param name="Roll">翻滚角 °</param>
/// <param name="Yaw">偏航角 °</param>
/// <param name="GravityX">重力向量 X 分量</param>
/// <param name="GravityY">重力向量 Y 分量</param>
/// <param name="GravityZ">重力向量 Z 分量</param>
/// <param name="Timestamp">采样时间</param>
public record DeviceOrientation(
    float Pitch,
    float Roll,
    float Yaw,
    float GravityX,
    float GravityY,
    float GravityZ,
    DateTime Timestamp
)
{
    /// <summary>人可读的朝向状态</summary>
    public DeviceOrientationState State => ComputeState(this.GravityX, this.GravityY, this.GravityZ);

    /// <summary>根据重力向量判断设备朝向状态</summary>
    private static DeviceOrientationState ComputeState(float gx, float gy, float gz)
    {
        float absX = Math.Abs(gx);
        float absY = Math.Abs(gy);
        float absZ = Math.Abs(gz);

        // Z 轴主导 → 平放或倒置
        if (absZ > absX && absZ > absY)
            return gz < 0 ? DeviceOrientationState.FlatFaceUp : DeviceOrientationState.FlatFaceDown;

        // X 轴主导 → 横屏
        if (absX > absY)
            return gx > 0 ? DeviceOrientationState.LandscapeRight : DeviceOrientationState.LandscapeLeft;

        // Y 轴主导 → 竖屏
        return gy > 0 ? DeviceOrientationState.PortraitUpsideDown : DeviceOrientationState.PortraitUpright;
    }

    /// <summary>中文状态描述</summary>
    public string StateLabel => this.State switch
    {
        DeviceOrientationState.PortraitUpright => "顶朝上",
        DeviceOrientationState.PortraitUpsideDown => "倒立",
        DeviceOrientationState.LandscapeLeft => "朝左",
        DeviceOrientationState.LandscapeRight => "朝右",
        DeviceOrientationState.FlatFaceUp => "平放",
        DeviceOrientationState.FlatFaceDown => "倒置",
        _ => "未知"
    };
}
