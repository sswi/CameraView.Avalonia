namespace CameraView.Models;

/// <summary>
/// 照片分辨率预设
/// </summary>
/// <param name="Width">宽度</param>
/// <param name="Height">高度</param>
/// <param name="Label">显示名称</param>
public record PhotoResolution(int Width, int Height, string Label)
{
    /// <summary>宽高比</summary>
    public double AspectRatio => (double)this.Width / this.Height;

    public override string ToString() => $"{this.Label} ({this.Width}x{this.Height})";

    /// <summary>默认可选分辨率列表</summary>
    public static readonly PhotoResolution[] DefaultPresets =
    [
        new(4032, 3024, "4:3 12MP"),
        new(3840, 2160, "16:9 8MP"),
        new(3264, 2448, "4:3 8MP"),
        new(1920, 1080, "16:9 2MP"),
        new(1600, 1200, "4:3 2MP"),
        new(1280,  960, "4:3 1.2MP"),
        new(1280,  720, "16:9 1MP"),
        new( 800,  600, "4:3 VGA"),
        new( 640,  480, "4:3 0.3MP"),
    ];
}
