using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;

namespace CameraView.Demo.Android;

[Activity(
    Label = "CameraView.Demo.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int CameraPermissionRequestCode = 1001;
    private CameraView.Services.ICameraProvider? cameraProvider;
    private bool wasCameraRunning;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Register BEFORE base.OnCreate — Avalonia init triggers ViewModel→Create() immediately
        var context = this.BaseContext;
        cameraProvider = new CameraView.Platforms.Android.AndroidCameraProvider(context);
        CameraView.CameraProviderFactory.RegisterProvider(cameraProvider);
        CameraView.CameraProviderFactory.RegisterOrientationFactory(
            () => new CameraView.Platforms.Android.AndroidDeviceOrientationProvider(context));

        // Register photo album saver
        CameraView.Demo.Services.PhotoAlbumSaverRegistry.Current =
            new CameraView.Demo.Android.Services.AndroidPhotoAlbumSaver(ContentResolver!);

        base.OnCreate(savedInstanceState);

        // Activity injection AFTER provider is registered
        CameraView.CameraProviderFactory.SetAndroidActivity(this);
    }

    protected override void OnPause()
    {
        base.OnPause();
        wasCameraRunning = cameraProvider?.IsInitialized == true;
        if (wasCameraRunning)
            _ = cameraProvider!.StopPreviewAsync();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (wasCameraRunning && cameraProvider != null)
        {
            wasCameraRunning = false;
            _ = cameraProvider.StartPreviewAsync();
        }
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        bool granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;

        // Forward to the cached AndroidCameraProvider so it can resolve the pending TaskCompletionSource
        CameraView.CameraProviderFactory.NotifyAndroidPermissionResult(granted);

        if (requestCode == CameraPermissionRequestCode)
        {
            System.Diagnostics.Debug.WriteLine(
                granted ? "Camera permission granted" : "Camera permission denied");
        }
    }
}
