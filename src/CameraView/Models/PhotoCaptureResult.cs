namespace CameraView.Models;

/// <summary>
/// 拍照结果
/// </summary>
/// <param name="IsSuccess">是否成功</param>
/// <param name="ImageData">JPEG 字节数据</param>
/// <param name="ErrorMessage">错误信息</param>
public record PhotoCaptureResult(
    bool IsSuccess,
    byte[]? ImageData,
    string? ErrorMessage
);
