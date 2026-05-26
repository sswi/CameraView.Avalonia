using AndroidX.Camera.Core;

namespace CameraView.Platforms.Android;

[Register("cameraview/platforms/android/ZoomObserver")]
public class ZoomObserver : Java.Lang.Object, IObserver
{
    private readonly Action<IZoomState> onChanged;

    public ZoomObserver(Action<IZoomState> onChanged)
    {
        this.onChanged = onChanged;
    }

    public void OnChanged(Java.Lang.Object? value)
    {
        if (value is IZoomState state)
            this.onChanged(state);
    }
}
