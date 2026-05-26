using CameraView.Services;

namespace CameraView;

public static class CameraProviderFactory
{
#if ANDROID
    private static ICameraProvider? cachedProvider;

    public static void SetAndroidActivity(object activity)
    {
        if (cachedProvider is ICameraActivityAware aware)
            aware.SetActivity(activity);
    }

    public static ICameraProvider Create()
    {
        if (cachedProvider != null)
            return cachedProvider;

        var context = global::Android.App.Application.Context;
        cachedProvider = new Platforms.Android.AndroidCameraProvider(context);
        return cachedProvider;
    }
#elif IOS
    public static ICameraProvider Create()
    {
        return new Platforms.iOS.iOSCameraProvider();
    }
#else
    public static ICameraProvider Create()
    {
        throw new PlatformNotSupportedException(
            "CameraView only supports Android and iOS platforms.");
    }
#endif

    public static ICameraPermissions CreatePermissions(ICameraProvider provider)
    {
        if (provider is ICameraPermissions perms)
            return perms;
        return new DefaultCameraPermissions();
    }

    private class DefaultCameraPermissions : ICameraPermissions
    {
        public Task<bool> CheckPermissionAsync() => Task.FromResult(true);
        public Task<bool> RequestPermissionAsync() => Task.FromResult(true);
    }
}
