namespace CameraView;

/// <summary>
/// 平台相机提供者工厂 — 根据当前平台创建对应的 ICameraProvider 实例
/// </summary>
public static class CameraProviderFactory
{
#if ANDROID
    private static ICameraProvider? cachedProvider;

    /// <summary>注入当前 Activity（用于权限请求）</summary>
    public static void SetAndroidActivity(object activity)
    {
        if (cachedProvider is ICameraActivityAware aware)
            aware.SetActivity(activity);
    }

    /// <summary>创建 Android 相机提供者（单例缓存）</summary>
    public static ICameraProvider Create()
    {
        if (cachedProvider != null)
            return cachedProvider;

        var context = Android.App.Application.Context;
        cachedProvider = new Platforms.Android.AndroidCameraProvider(context);
        return cachedProvider;
    }
#elif IOS
    public static ICameraProvider Create()
    {
        return new Platforms.iOS.iOSCameraProvider();
    }
#elif WINDOWS
    public static ICameraProvider Create()
    {
        return new Platforms.Windows.WindowsCameraProvider();
    }
#elif LINUX
    public static ICameraProvider Create()
    {
        return new Platforms.Linux.LinuxCameraProvider();
    }
#else
    public static ICameraProvider Create()
    {
        throw new PlatformNotSupportedException("CameraView 仅支持 Windows、Linux、Android 和 iOS 平台。");
    }
#endif

    /// <summary>创建设备朝向传感器提供者</summary>
    public static IDeviceOrientationProvider? CreateOrientationProvider()
    {
#if ANDROID
        var context = Android.App.Application.Context;
        return new Platforms.Android.AndroidDeviceOrientationProvider(context);
#elif IOS
        return new Platforms.iOS.iOSDeviceOrientationProvider();
#else
        return null;
#endif
    }

    /// <summary>创建相机权限处理器（如果提供者自身实现了权限接口则直接返回）</summary>
    public static ICameraPermissions CreatePermissions(ICameraProvider provider)
    {
        if (provider is ICameraPermissions perms) return perms;
        return new DefaultCameraPermissions();
    }

    /// <summary>桌面端默认权限（始终返回 true）</summary>
    private class DefaultCameraPermissions : ICameraPermissions
    {
        public Task<bool> CheckPermissionAsync() => Task.FromResult(true);
        public Task<bool> RequestPermissionAsync() => Task.FromResult(true);
    }
}
