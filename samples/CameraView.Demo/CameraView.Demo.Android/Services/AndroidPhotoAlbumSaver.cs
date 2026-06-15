using System;
using System.Threading.Tasks;
using Android.Content;
using Android.Provider;
using CameraView.Demo.Services;

namespace CameraView.Demo.Android.Services;

public sealed class AndroidPhotoAlbumSaver : IPhotoAlbumSaver
{
    private readonly ContentResolver contentResolver;

    public AndroidPhotoAlbumSaver(ContentResolver contentResolver)
    {
        this.contentResolver = contentResolver;
    }

    public Task<string?> SavePhotoAsync(byte[] imageBytes)
    {
        try
        {
            var contentValues = new ContentValues();
            contentValues.Put(MediaStore.Images.Media.InterfaceConsts.DisplayName,
                $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            contentValues.Put(MediaStore.Images.Media.InterfaceConsts.MimeType, "image/jpeg");
            contentValues.Put(MediaStore.Images.Media.InterfaceConsts.RelativePath, "Pictures/CameraView");
            contentValues.Put(MediaStore.Images.Media.InterfaceConsts.IsPending, 1);

            var uri = contentResolver.Insert(MediaStore.Images.Media.ExternalContentUri, contentValues);
            if (uri == null)
                return Task.FromResult<string?>("无法创建 MediaStore 条目");

            using var stream = contentResolver.OpenOutputStream(uri);
            if (stream == null)
                return Task.FromResult<string?>("无法打开输出流");

            stream.Write(imageBytes, 0, imageBytes.Length);
            stream.Flush();

            contentValues.Clear();
            contentValues.Put(MediaStore.Images.Media.InterfaceConsts.IsPending, 0);
            contentResolver.Update(uri, contentValues, null, null);

            return Task.FromResult<string?>(null); // success
        }
        catch (System.Exception ex)
        {
            return Task.FromResult<string?>(ex.Message);
        }
    }
}
