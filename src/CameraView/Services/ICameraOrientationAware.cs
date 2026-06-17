namespace CameraView.Services;

/// <summary>
/// 相机提供者可选的朝向感知接口 — 由 CameraViewControl 在收到重力传感器数据时调用
/// 各平台实现照片旋转校正（同 iOS RotatePhotoData / Android RotatePhotoData）
/// </summary>
public interface ICameraOrientationAware
{
    void UpdateDeviceOrientation(DeviceOrientationState state);
}
