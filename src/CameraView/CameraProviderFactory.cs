using CameraView.Services;

namespace CameraView;

public static class CameraProviderFactory
{
    private static Func<ICameraProvider>? androidFactory;
    private static ICameraProvider? cachedProvider;

    public static void RegisterAndroidProvider(Func<ICameraProvider> factory)
    {
        androidFactory = factory;
    }

    public static void SetAndroidActivity(object activity)
    {
        if (cachedProvider is ICameraActivityAware aware)
        {
            aware.SetActivity(activity);
        }
    }

    public static ICameraProvider Create()
    {
#if ANDROID
        if (cachedProvider != null)
            return cachedProvider;

        if (androidFactory != null)
        {
            cachedProvider = androidFactory();
            return cachedProvider;
        }

        throw new InvalidOperationException(
            "Android camera provider not registered.");
#elif IOS
        return new Platforms.iOS.iOSCameraProvider();
#else
        throw new PlatformNotSupportedException(
            "CameraView only supports Android and iOS platforms.");
#endif
    }

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
