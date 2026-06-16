namespace CameraView;

/// <summary>
/// 平台相机提供者工厂 — 根据当前平台创建对应的 ICameraProvider 实例
/// </summary>
public static class CameraProviderFactory
{
#if ANDROID
    private static ICameraProvider? cachedProvider;
    private static Func<IDeviceOrientationProvider?>? orientationFactory;
    private static object? pendingActivity;

    /// <summary>注册 Android 相机提供者（由 MainActivity 调用）</summary>
    public static void RegisterProvider(ICameraProvider provider)
    {
        cachedProvider = provider;
    }

    /// <summary>注册方向传感器工厂（由 MainActivity 调用）</summary>
    public static void RegisterOrientationFactory(Func<IDeviceOrientationProvider?> factory)
    {
        orientationFactory = factory;
    }

    /// <summary>注入当前 Activity（用于权限请求）</summary>
    public static void SetAndroidActivity(object activity)
    {
        if (cachedProvider is ICameraActivityAware aware)
            aware.SetActivity(activity);
        else
            pendingActivity = activity;
    }

    /// <summary>通知 Android 权限请求结果</summary>
    public static void NotifyAndroidPermissionResult(bool granted)
    {
        if (cachedProvider != null)
        {
            var method = cachedProvider.GetType().GetMethod("NotifyPermissionResult",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            method?.Invoke(cachedProvider, [granted]);
        }
    }

    public static ICameraProvider Create()
    {
        if (cachedProvider != null)
        {
            if (pendingActivity != null)
            {
                ((ICameraActivityAware)cachedProvider).SetActivity(pendingActivity);
                pendingActivity = null;
            }
            return cachedProvider;
        }
        throw new InvalidOperationException("Android provider not registered. Call RegisterProvider in MainActivity.OnCreate.");
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
#elif MACOS
    public static ICameraProvider Create()
    {
        return new Platforms.macOS.MacCameraProvider();
    }
#elif BROWSER
    public static ICameraProvider Create()
    {
        return new Platforms.Browser.BrowserCameraProvider();
    }
#else
    public static ICameraProvider Create()
    {
        throw new PlatformNotSupportedException($"CameraView 不支持当前平台。");
    }
#endif

    /// <summary>创建设备朝向传感器提供者</summary>
    public static IDeviceOrientationProvider? CreateOrientationProvider()
    {
#if ANDROID
        return orientationFactory?.Invoke();
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
