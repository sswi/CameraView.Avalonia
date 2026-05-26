namespace CameraView.Models;

public record PhotoCaptureResult(
    bool IsSuccess,
    byte[]? ImageData,
    string? ErrorMessage
);
