using System;
using Android.Content;
using Android.Hardware;
using CameraView.Models;
using CameraView.Services;

namespace CameraView.Platforms.Android;

public class AndroidDeviceOrientationProvider : Java.Lang.Object, ISensorEventListener, IDeviceOrientationProvider
{
    private readonly SensorManager? sensorManager;
    private readonly Sensor? rotationSensor;
    private readonly float[] rotationMatrix = new float[16];
    private readonly float[] orientationValues = new float[3];

    public DeviceOrientation? CurrentOrientation { get; private set; }
    public event Action<DeviceOrientation>? OrientationChanged;

    public AndroidDeviceOrientationProvider(Context context)
    {
        this.sensorManager = context.GetSystemService(Context.SensorService) as SensorManager;
        this.rotationSensor = this.sensorManager?.GetDefaultSensor(SensorType.RotationVector);
    }

    public void Start()
    {
        if (this.sensorManager != null && this.rotationSensor != null)
        {
            this.sensorManager.RegisterListener(
                this,
                this.rotationSensor,
                SensorDelay.Game); // ~20ms updates
        }
    }

    public void Stop()
    {
        this.sensorManager?.UnregisterListener(this);
    }

    public void OnSensorChanged(SensorEvent e)
    {
        if (e.Sensor?.Type != SensorType.RotationVector) return;
        if (e.Values == null || e.Values.Count < 4) return;

        try
        {
            float[]? rotation = null;
            if (e.Values.Count >= 9)
            {
                // Direct rotation matrix
                var vals = new float[e.Values.Count];
                e.Values.CopyTo(vals, 0);
                rotation = vals[..9];
            }
            else
            {
                // Quaternion from rotation vector
                float[] quat = [e.Values[0], e.Values[1], e.Values[2], e.Values[3]];
                rotation = new float[9];
                SensorManager.GetRotationMatrixFromVector(rotation, quat);
            }

            // Use default device coordinate system (matches screen orientation)
            SensorManager.GetOrientation(rotation, this.orientationValues);

            // orientationValues: [0]=Azimuth(yaw), [1]=Pitch, [2]=Roll (radians)
            float pitch = MathF.Round(RadToDeg(this.orientationValues[1]), 1);
            float roll = MathF.Round(RadToDeg(this.orientationValues[2]), 1);
            float yaw = MathF.Round(RadToDeg(this.orientationValues[0]), 1);

            // Gravity = inverse of world-up = -column2 of rotation matrix
            float gx = MathF.Round(-rotation[6], 3);
            float gy = MathF.Round(-rotation[7], 3);
            float gz = MathF.Round(-rotation[8], 3);

            var orientation = new DeviceOrientation(pitch, roll, yaw, gx, gy, gz, DateTime.Now);
            this.CurrentOrientation = orientation;
            this.OrientationChanged?.Invoke(orientation);
        }
        catch { }
    }

    public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy) { }

    public void Dispose()
    {
        this.Stop();
    }

    private static float RadToDeg(float rad) => rad * 180f / MathF.PI;
}
