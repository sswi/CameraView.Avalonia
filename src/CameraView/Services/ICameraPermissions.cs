namespace CameraView.Services;

public interface ICameraPermissions
{
    Task<bool> CheckPermissionAsync();
    Task<bool> RequestPermissionAsync();
}
