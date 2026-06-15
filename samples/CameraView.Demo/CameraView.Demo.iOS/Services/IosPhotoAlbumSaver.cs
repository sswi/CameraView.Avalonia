using System;
using System.Threading.Tasks;
using CameraView.Demo.Services;
using Foundation;
using Photos;
using UIKit;

namespace CameraView.Demo.iOS.Services;

public sealed class IosPhotoAlbumSaver : IPhotoAlbumSaver
{
    public async Task<string?> SavePhotoAsync(byte[] imageBytes)
    {
        var hasPermission = await EnsurePhotoLibraryAddPermissionAsync().ConfigureAwait(false);
        if (!hasPermission)
            return "无相册权限，请在系统设置中授予相册写入权限后重试。";

        var imageData = NSData.FromArray(imageBytes);
        var image = UIImage.LoadFromData(imageData);
        if (image == null)
        {
            imageData.Dispose();
            return "照片解码失败，UIImage.LoadFromData 返回 null";
        }

        try
        {
            return await SaveImageToPhotoLibraryAsync(image, imageData).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            image.Dispose();
            imageData.Dispose();
            return ex.Message;
        }
    }

    private static async Task<bool> EnsurePhotoLibraryAddPermissionAsync()
    {
        var status = GetPhotoLibraryAuthorizationStatus();
        if (IsPhotoLibraryWriteAuthorized(status))
            return true;

        if (status == PHAuthorizationStatus.Denied || status == PHAuthorizationStatus.Restricted)
            return false;

        status = await RequestPhotoLibraryAuthorizationAsync().ConfigureAwait(false);
        return IsPhotoLibraryWriteAuthorized(status);
    }

    private static PHAuthorizationStatus GetPhotoLibraryAuthorizationStatus()
    {
        if (OperatingSystem.IsIOSVersionAtLeast(14))
            return PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.AddOnly);

        return PHPhotoLibrary.AuthorizationStatus;
    }

    private static Task<PHAuthorizationStatus> RequestPhotoLibraryAuthorizationAsync()
    {
        var tcs = new TaskCompletionSource<PHAuthorizationStatus>();

        if (OperatingSystem.IsIOSVersionAtLeast(14))
        {
            PHPhotoLibrary.RequestAuthorization(PHAccessLevel.AddOnly, status => tcs.TrySetResult(status));
        }
        else
        {
            PHPhotoLibrary.RequestAuthorization(status => tcs.TrySetResult(status));
        }

        return tcs.Task;
    }

    private static bool IsPhotoLibraryWriteAuthorized(PHAuthorizationStatus status)
    {
        return status == PHAuthorizationStatus.Authorized || status == PHAuthorizationStatus.Limited;
    }

    private static Task<string?> SaveImageToPhotoLibraryAsync(UIImage image, NSData imageData)
    {
        var tcs = new TaskCompletionSource<string?>();

        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            try
            {
                PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(() =>
                {
                    _ = PHAssetChangeRequest.FromImage(image);
                }, (success, nsError) =>
                {
                    try
                    {
                        tcs.TrySetResult(success ? null : nsError?.LocalizedDescription ?? "未知错误");
                    }
                    finally
                    {
                        image.Dispose();
                        imageData.Dispose();
                    }
                });
            }
            catch (Exception ex)
            {
                image.Dispose();
                imageData.Dispose();
                tcs.TrySetResult(ex.Message);
            }
        });

        return tcs.Task;
    }
}
