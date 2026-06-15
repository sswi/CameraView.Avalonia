using Foundation;
using UIKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using CameraView.Demo;
using CameraView.Demo.iOS.Services;
using CameraView.Demo.Services;

namespace CameraView.Demo.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        PhotoAlbumSaverRegistry.Current = new IosPhotoAlbumSaver();

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
