using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace CameraView.Demo.Android;

[Application]
public class Application : AvaloniaAndroidApplication<App>
{
    public Application(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Register CameraView Android provider before Avalonia starts
        CameraView.CameraProviderFactory.RegisterAndroidProvider(() =>
        {
            var context = global::Android.App.Application.Context;
            return new AndroidCameraProvider(context);
        });

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
