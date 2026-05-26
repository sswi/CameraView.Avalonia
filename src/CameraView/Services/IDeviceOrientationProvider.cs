using CameraView.Models;

namespace CameraView.Services;

public interface IDeviceOrientationProvider : IDisposable
{
    DeviceOrientation? CurrentOrientation { get; }
    event Action<DeviceOrientation>? OrientationChanged;

    void Start();
    void Stop();
}
