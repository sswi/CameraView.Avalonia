using Android.Runtime;
using AndroidX.Lifecycle;

namespace CameraView.Platforms.Android;

[Register("cameraview/platforms/android/ForeverLifecycleOwner")]
public class ForeverLifecycleOwner : Java.Lang.Object, ILifecycleOwner
{
    private readonly LifecycleRegistry registry;

    public ForeverLifecycleOwner()
    {
        this.registry = new LifecycleRegistry(this);
        this.registry.HandleLifecycleEvent(Lifecycle.Event.OnStart);
        this.registry.HandleLifecycleEvent(Lifecycle.Event.OnResume);
    }

    public Lifecycle Lifecycle => this.registry;
}
