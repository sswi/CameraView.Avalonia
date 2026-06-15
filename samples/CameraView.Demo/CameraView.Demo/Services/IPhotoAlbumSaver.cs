using System.Threading.Tasks;

namespace CameraView.Demo.Services;

public interface IPhotoAlbumSaver
{
    Task<string?> SavePhotoAsync(byte[] imageBytes);
}
