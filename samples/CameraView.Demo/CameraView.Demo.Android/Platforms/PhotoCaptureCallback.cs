using AndroidX.Camera.Core;
using System;

namespace CameraView.Platforms.Android;

[Register("cameraview/platforms/android/PhotoCaptureCallback")]
public class PhotoCaptureCallback : Java.Lang.Object, ImageCapture.IOnImageSavedCallback
{
    private readonly Action<ImageCapture.OutputFileResults> onSaved;
    private readonly Action<string> onError;

    public PhotoCaptureCallback(
        Action<ImageCapture.OutputFileResults> onSaved,
        Action<string> onError)
    {
        this.onSaved = onSaved;
        this.onError = onError;
    }

    public void OnCaptureStarted()
    {
        // No-op: required by interface but we don't need it
    }

    public void OnImageSaved(ImageCapture.OutputFileResults outputFileResults)
    {
        this.onSaved(outputFileResults);
    }

    public void OnError(ImageCaptureException exception)
    {
        this.onError(exception.Message ?? "Photo capture failed");
    }
}
