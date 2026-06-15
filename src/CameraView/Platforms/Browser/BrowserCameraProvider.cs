#if BROWSER
using System.Runtime.InteropServices.JavaScript;
using CameraView.Models;
using CameraView.Services;
using SkiaSharp;

namespace CameraView.Platforms.Browser;

/// <summary>
/// 浏览器 (WASM) 相机提供者 — 基于 WebRTC getUserMedia + Canvas 2D
/// 按 deviceId 精确切换多摄像头
/// </summary>
internal partial class BrowserCameraProvider : ICameraProvider
{
    private static bool moduleImported;
    private static readonly object moduleLock = new();

    private static async Task EnsureModuleAsync()
    {
        if (moduleImported) return;
        lock (moduleLock) { if (moduleImported) return; moduleImported = true; }
        await JSHost.ImportAsync("cameraModule", "../main.js");
    }

    [JSImport("startCamera", "cameraModule")]
    private static partial Task<bool> StartCameraJS(string deviceId, int width, int height);

    [JSImport("stopCamera", "cameraModule")]
    private static partial void StopCameraJS();

    [JSImport("getFrameData", "cameraModule")]
    private static partial byte[]? GetFrameDataJS();

    [JSImport("capturePhoto", "cameraModule")]
    private static partial string CapturePhotoJS();

    [JSImport("enumerateCameras", "cameraModule")]
    private static partial Task<string> EnumerateCamerasJS();

    [JSImport("getSupportedResolutions", "cameraModule")]
    private static partial string GetSupportedResolutionsJS();

    [JSImport("getCameraInfo", "cameraModule")]
    private static partial string GetCameraInfoJS();

    // ========================================================================
    //  成员变量
    // ========================================================================

    private int frameWidth = 640;
    private int frameHeight = 480;
    private CancellationTokenSource? cts;
    private bool started;
    private List<string> cameraIds = [];
    private List<PhotoResolution> supportedPhotoResolutions = [.. PhotoResolution.DefaultPresets];
    private int currentCameraIndex;

    public bool IsInitialized => true;
    public CameraFacing CurrentFacing { get; private set; } = CameraFacing.Back;
    public float? MinZoomFactor { get; private set; }
    public float? MaxZoomFactor { get; private set; }
    public float? CurrentZoomFactor { get; private set; }
    public FlashMode FlashMode { get; private set; } = FlashMode.Auto;
    public PhotoResolution PhotoResolution { get; private set; } = PhotoResolution.DefaultPresets[0];
    public IReadOnlyList<PhotoResolution> SupportedPhotoResolutions => this.supportedPhotoResolutions;
    public float MinExposureCompensation { get; private set; }
    public float MaxExposureCompensation { get; private set; }
    public float ExposureCompensation { get; private set; }

    public event EventHandler<byte[]>? PhotoCaptured;
    public event EventHandler<SKBitmap>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;

    // ========================================================================
    //  初始化
    // ========================================================================

    public async Task InitializeAsync(CameraOptions? options = null)
    {
        await EnsureModuleAsync();
        if (options?.CameraFacing == CameraFacing.Front)
            CurrentFacing = CameraFacing.Front;

        // 枚举可用摄像头
        try
        {
            var json = await EnumerateCamerasJS();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            this.cameraIds = doc.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("deviceId").GetString())
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList()!;
            this.currentCameraIndex = 0;
            this.supportedPhotoResolutions = [.. PhotoResolution.DefaultPresets];
        }
        catch { }
    }

    // ========================================================================
    //  预览
    // ========================================================================

    public async Task StartPreviewAsync()
    {
        // 防止 CameraEnabled 绑定导致的双重调用（第二次 stopCamera 会清空 JS 状态）
        if (this.started) return;
        try
        {
            this.PhotoResolution = this.GetBestSupportedPhotoResolution(this.PhotoResolution);
            var id = this.cameraIds.Count > 0
                ? this.cameraIds[this.currentCameraIndex] : "";
            bool ok = await StartCameraJS(id, this.PhotoResolution.Width, this.PhotoResolution.Height);

            if (!ok && this.cameraIds.Count > 1)
            {
                for (int i = 0; i < this.cameraIds.Count; i++)
                {
                    if (i == this.currentCameraIndex) continue;
                    ok = await StartCameraJS(this.cameraIds[i], this.PhotoResolution.Width, this.PhotoResolution.Height);
                    if (ok) { this.currentCameraIndex = i; break; }
                }
            }

            // 最后兜底：不指定 deviceId，让浏览器自动选
            if (!ok)
            {
                ok = await StartCameraJS("", this.PhotoResolution.Width, this.PhotoResolution.Height);
            }

            if (!ok)
            {
                this.ErrorOccurred?.Invoke(this, "浏览器拒绝访问摄像头。");
                return;
            }

            this.RefreshRuntimeCameraState();
            this.started = true;
            this.cts = new CancellationTokenSource();
            _ = FrameLoopAsync(this.cts.Token);
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"启动预览失败: {ex.Message}");
        }
    }

    private async Task FrameLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var data = GetFrameDataJS();
                if (data == null || data.Length == 0) continue;

                var bmp = new SKBitmap(this.frameWidth, this.frameHeight,
                    SKColorType.Rgba8888, SKAlphaType.Unpremul);

                unsafe
                {
                    fixed (byte* p = data)
                    {
                        global::System.Buffer.MemoryCopy(p, bmp.GetPixels().ToPointer(),
                            data.Length, data.Length);
                    }
                }

                this.FrameReceived?.Invoke(this, bmp);
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task StopPreviewAsync()
    {
        this.started = false;
        this.cts?.Cancel();
        this.cts?.Dispose();
        this.cts = null;
        StopCameraJS();
        return Task.CompletedTask;
    }

    // ========================================================================
    //  拍照
    // ========================================================================

    public Task TakePhotoAsync()
    {
        try
        {
            var dataUrl = CapturePhotoJS();
            if (string.IsNullOrEmpty(dataUrl)) return Task.CompletedTask;

            var base64 = dataUrl.Substring(dataUrl.IndexOf(',') + 1);
            var jpeg = Convert.FromBase64String(base64);
            this.PhotoCaptured?.Invoke(this, jpeg);
        }
        catch (Exception ex)
        {
            this.ErrorOccurred?.Invoke(this, $"拍照失败: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    // ========================================================================
    //  切换摄像头（按设备列表循环）
    // ========================================================================

    public async Task SwitchCameraAsync(CameraFacing facing)
    {
        if (this.cameraIds.Count <= 1) return;

        var wasRunning = this.started;
        await StopPreviewAsync();

        this.currentCameraIndex = (this.currentCameraIndex + 1) % this.cameraIds.Count;

        if (wasRunning)
            await StartPreviewAsync();
    }

    // ========================================================================
    //  不支持的功能
    // ========================================================================

    public Task SetFocusAsync(float normalizedX, float normalizedY) => Task.CompletedTask;
    public Task SetTorchAsync(bool on) => Task.CompletedTask;
    public Task SetZoomAsync(float zoomFactor) => Task.CompletedTask;
    public Task SetFlashModeAsync(FlashMode mode) { FlashMode = mode; return Task.CompletedTask; }
    public async Task SetPhotoResolutionAsync(PhotoResolution resolution)
    {
        this.PhotoResolution = this.GetBestSupportedPhotoResolution(resolution);

        if (this.started)
        {
            await StopPreviewAsync();
            await StartPreviewAsync();
        }
    }
    public Task SetExposureCompensationAsync(float ev) { ExposureCompensation = ev; return Task.CompletedTask; }

    public void Dispose()
    {
        StopPreviewAsync();
    }

    private void RefreshRuntimeCameraState()
    {
        try
        {
            var supportedJson = GetSupportedResolutionsJS();
            if (!string.IsNullOrWhiteSpace(supportedJson))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(supportedJson);
                var resolutions = doc.RootElement.EnumerateArray()
                    .Select(e => new PhotoResolution(
                        e.GetProperty("width").GetInt32(),
                        e.GetProperty("height").GetInt32(),
                        e.GetProperty("label").GetString() ?? string.Empty))
                    .DistinctBy(r => (r.Width, r.Height))
                    .OrderByDescending(r => r.Width * r.Height)
                    .ThenByDescending(r => r.Width)
                    .ToList();

                if (resolutions.Count > 0)
                    this.supportedPhotoResolutions = resolutions;
            }

            var infoJson = GetCameraInfoJS();
            if (!string.IsNullOrWhiteSpace(infoJson))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(infoJson);
                if (doc.RootElement.TryGetProperty("width", out var width))
                    this.frameWidth = width.GetInt32();
                if (doc.RootElement.TryGetProperty("height", out var height))
                    this.frameHeight = height.GetInt32();
                if (doc.RootElement.TryGetProperty("deviceId", out var deviceIdProperty))
                {
                    var activeDeviceId = deviceIdProperty.GetString();
                    var index = this.cameraIds.FindIndex(id => id == activeDeviceId);
                    if (index >= 0)
                        this.currentCameraIndex = index;
                }
            }

            this.PhotoResolution = this.GetBestSupportedPhotoResolution(this.PhotoResolution);
        }
        catch
        {
        }
    }

    private PhotoResolution GetBestSupportedPhotoResolution(PhotoResolution preferred)
    {
        if (this.supportedPhotoResolutions.Count == 0)
            return preferred;

        var exact = this.supportedPhotoResolutions.FirstOrDefault(r => r.Width == preferred.Width && r.Height == preferred.Height);
        if (exact != null)
            return exact;

        var preferredPixels = preferred.Width * preferred.Height;
        return this.supportedPhotoResolutions
            .OrderBy(r => Math.Abs((r.Width * r.Height) - preferredPixels))
            .ThenBy(r => Math.Abs(r.AspectRatio - preferred.AspectRatio))
            .First();
    }
}
#endif
