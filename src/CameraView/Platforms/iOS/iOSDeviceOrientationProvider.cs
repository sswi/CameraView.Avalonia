using System;
using CoreMotion;
using CameraView.Models;
using CameraView.Services;

namespace CameraView.Platforms.iOS;

public class iOSDeviceOrientationProvider : IDeviceOrientationProvider
{
    private readonly CMMotionManager motionManager = new();

    public DeviceOrientation? CurrentOrientation { get; private set; }
    public event Action<DeviceOrientation>? OrientationChanged;

    public void Start()
    {
        if (!this.motionManager.DeviceMotionAvailable) return;

        this.motionManager.DeviceMotionUpdateInterval = 0.05; // 20Hz
        this.motionManager.StartDeviceMotionUpdates(
            NSOperationQueue.CurrentQueue ?? new NSOperationQueue(),
            (motion, _) =>
            {
                if (motion == null) return;

                try
                {
                    var attitude = motion.Attitude;
                    var gravity = motion.Gravity;

                    float pitch = MathF.Round(RadToDeg((float)attitude.Pitch), 1);
                    float roll = MathF.Round(RadToDeg((float)attitude.Roll), 1);
                    float yaw = MathF.Round(RadToDeg((float)attitude.Yaw), 1);
                    float gx = MathF.Round((float)gravity.X, 3);
                    float gy = MathF.Round((float)gravity.Y, 3);
                    float gz = MathF.Round((float)gravity.Z, 3);

                    var orientation = new DeviceOrientation(pitch, roll, yaw, gx, gy, gz, DateTime.Now);
                    this.CurrentOrientation = orientation;
                    this.OrientationChanged?.Invoke(orientation);
                }
                catch { }
            });
    }

    public void Stop()
    {
        this.motionManager.StopDeviceMotionUpdates();
    }

    public void Dispose()
    {
        this.Stop();
        this.motionManager.Dispose();
    }

    private static float RadToDeg(float rad) => rad * 180f / MathF.PI;
}
