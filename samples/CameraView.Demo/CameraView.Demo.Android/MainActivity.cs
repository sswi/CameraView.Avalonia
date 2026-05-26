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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Register this Activity with the camera provider
        CameraView.CameraProviderFactory.SetAndroidActivity(this);

        // Check and request camera permission on first launch
        if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.Camera)
            != Permission.Granted)
        {
            ActivityCompat.RequestPermissions(
                this,
                [global::Android.Manifest.Permission.Camera],
                CameraPermissionRequestCode);
        }
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode == CameraPermissionRequestCode)
        {
            if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
            {
                System.Diagnostics.Debug.WriteLine("Camera permission granted");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("Camera permission denied");
            }
        }
    }
}
