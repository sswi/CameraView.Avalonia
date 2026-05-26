using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Windows.Input;
using CameraView.Models;
using CameraView.Services;
using SkiaSharp;

namespace CameraView;

public class CameraViewControl : TemplatedControl
{
    // --- Template Parts ---
    private Image? previewImage;
    private Ellipse? focusIndicator;
    private Canvas? focusCanvas;
    private TextBlock? fpsTextBlock;

    // --- Gesture state ---
    private readonly Dictionary<long, Point> activePointers = new();
    private Point potentialTapPoint;
    private DateTime potentialTapTime;
    private bool isPinching;
    private double pinchStartDistance;
    private float pinchStartZoom = 1f;

    // --- Services ---
    private ICameraProvider? cameraProvider;
    private FrameProcessor? frameProcessor;

    // --- Styled Properties ---

    public static readonly StyledProperty<bool> CameraEnabledProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(CameraEnabled), false);

    public bool CameraEnabled
    {
        get => this.GetValue(CameraEnabledProperty);
        set => this.SetValue(CameraEnabledProperty, value);
    }

    public static readonly StyledProperty<CameraFacing> CameraFacingProperty =
        AvaloniaProperty.Register<CameraViewControl, CameraFacing>(nameof(CameraFacing), CameraFacing.Back);

    public CameraFacing CameraFacing
    {
        get => this.GetValue(CameraFacingProperty);
        set => this.SetValue(CameraFacingProperty, value);
    }

    public static readonly StyledProperty<bool> TorchOnProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(TorchOn), false);

    public bool TorchOn
    {
        get => this.GetValue(TorchOnProperty);
        set => this.SetValue(TorchOnProperty, value);
    }

    public static readonly StyledProperty<float?> RequestZoomFactorProperty =
        AvaloniaProperty.Register<CameraViewControl, float?>(nameof(RequestZoomFactor));

    public float? RequestZoomFactor
    {
        get => this.GetValue(RequestZoomFactorProperty);
        set => this.SetValue(RequestZoomFactorProperty, value);
    }

    public static readonly StyledProperty<float?> CurrentZoomFactorProperty =
        AvaloniaProperty.Register<CameraViewControl, float?>(nameof(CurrentZoomFactor));

    public float? CurrentZoomFactor
    {
        get => this.GetValue(CurrentZoomFactorProperty);
        set => this.SetValue(CurrentZoomFactorProperty, value);
    }

    public static readonly StyledProperty<bool> TapToFocusEnabledProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(TapToFocusEnabled), true);

    public bool TapToFocusEnabled
    {
        get => this.GetValue(TapToFocusEnabledProperty);
        set => this.SetValue(TapToFocusEnabledProperty, value);
    }

    public static readonly StyledProperty<bool> PinchToZoomEnabledProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(PinchToZoomEnabled), true);

    public bool PinchToZoomEnabled
    {
        get => this.GetValue(PinchToZoomEnabledProperty);
        set => this.SetValue(PinchToZoomEnabledProperty, value);
    }

    public static readonly StyledProperty<bool> IsCapturingNextFrameProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(
            nameof(IsCapturingNextFrame),
            false,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public bool IsCapturingNextFrame
    {
        get => this.GetValue(IsCapturingNextFrameProperty);
        set => this.SetValue(IsCapturingNextFrameProperty, value);
    }

    public static readonly StyledProperty<bool> IsBusyingProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(
            nameof(IsBusying),
            false,
            defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    public bool IsBusying
    {
        get => this.GetValue(IsBusyingProperty);
        private set => this.SetValue(IsBusyingProperty, value);
    }

    public static readonly StyledProperty<ICameraProvider?> CameraProviderProperty =
        AvaloniaProperty.Register<CameraViewControl, ICameraProvider?>(nameof(CameraProvider));

    public ICameraProvider? CameraProvider
    {
        get => this.GetValue(CameraProviderProperty);
        set => this.SetValue(CameraProviderProperty, value);
    }

    public static readonly StyledProperty<ICommand?> PhotoCapturedCommandProperty =
        AvaloniaProperty.Register<CameraViewControl, ICommand?>(
            nameof(PhotoCapturedCommand));

    public ICommand? PhotoCapturedCommand
    {
        get => this.GetValue(PhotoCapturedCommandProperty);
        set => this.SetValue(PhotoCapturedCommandProperty, value);
    }

    // --- Events ---

    public event EventHandler<byte[]>? PhotoCaptured;
    public event EventHandler<string>? CameraError;

    // --- Constructor ---

    public static readonly StyledProperty<bool> DebugModeProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(DebugMode), false);

    public bool DebugMode
    {
        get => this.GetValue(DebugModeProperty);
        set => this.SetValue(DebugModeProperty, value);
    }

    public static readonly StyledProperty<FlashMode> FlashModeProperty =
        AvaloniaProperty.Register<CameraViewControl, FlashMode>(nameof(FlashMode), Models.FlashMode.Auto);

    public FlashMode FlashMode
    {
        get => this.GetValue(FlashModeProperty);
        set => this.SetValue(FlashModeProperty, value);
    }

    public static readonly StyledProperty<PhotoResolution?> PhotoResolutionProperty =
        AvaloniaProperty.Register<CameraViewControl, PhotoResolution?>(nameof(PhotoResolution));

    public PhotoResolution? PhotoResolution
    {
        get => this.GetValue(PhotoResolutionProperty);
        set => this.SetValue(PhotoResolutionProperty, value);
    }

    public static readonly DirectProperty<CameraViewControl, IReadOnlyList<PhotoResolution>> SupportedResolutionsProperty =
        AvaloniaProperty.RegisterDirect<CameraViewControl, IReadOnlyList<PhotoResolution>>(
            nameof(SupportedResolutions),
            o => o.SupportedResolutions);

    public IReadOnlyList<PhotoResolution> SupportedResolutions =>
        this.cameraProvider?.SupportedPhotoResolutions ?? [];

    public CameraViewControl()
    {
        this.frameProcessor = new FrameProcessor();
        this.frameProcessor.FrameReady += this.OnFrameReady;
        this.frameProcessor.FpsUpdated += this.OnFpsUpdated;
    }

    // --- Template Binding ---

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        this.previewImage = e.NameScope.Find<Image>("PART_PreviewImage");
        this.focusIndicator = e.NameScope.Find<Ellipse>("PART_FocusIndicator");
        this.focusCanvas = e.NameScope.Find<Canvas>("PART_FocusCanvas");
        this.fpsTextBlock = e.NameScope.Find<TextBlock>("PART_FpsText");

        this.AddHandler(PointerPressedEvent, this.OnPointerPressed, RoutingStrategies.Tunnel);
        this.AddHandler(PointerMovedEvent, this.OnPointerMoved, RoutingStrategies.Tunnel);
        this.AddHandler(PointerReleasedEvent, this.OnPointerReleased, RoutingStrategies.Tunnel);

        this.RunInitializationAsync();
    }

    private async void RunInitializationAsync()
    {
        if (this.CameraProvider != null)
        {
            await this.InitializeCameraAsync(this.CameraProvider);
        }
    }

    // --- Public API ---

    public async Task InitializeCameraAsync(ICameraProvider provider)
    {
        this.cameraProvider = provider;
        this.cameraProvider.FrameReceived += this.OnFrameReceived;
        this.cameraProvider.PhotoCaptured += this.OnPhotoCaptured;
        this.cameraProvider.ErrorOccurred += this.OnCameraError;

        await this.cameraProvider.InitializeAsync(new CameraOptions
        {
            CameraFacing = this.CameraFacing
        });
    }

    public async Task StartCameraAsync()
    {
        if (this.cameraProvider != null)
        {
            this.IsBusying = true;
            await this.cameraProvider.StartPreviewAsync();
            this.IsBusying = false;
        }
    }

    public async Task StopCameraAsync()
    {
        if (this.cameraProvider != null)
        {
            this.IsBusying = true;
            await this.cameraProvider.StopPreviewAsync();
            this.IsBusying = false;
        }
    }

    public async Task TakePhotoAsync()
    {
        if (this.cameraProvider != null)
        {
            this.IsBusying = true;
            await this.cameraProvider.TakePhotoAsync();
        }
    }

    public async Task SwitchCameraAsync()
    {
        if (this.cameraProvider != null)
        {
            this.IsBusying = true;
            var newFacing = this.cameraProvider.CurrentFacing == CameraFacing.Back
                ? CameraFacing.Front : CameraFacing.Back;
            await this.cameraProvider.SwitchCameraAsync(newFacing);
            this.IsBusying = false;
        }
    }

    // --- Frame Handling ---

    private void OnFrameReceived(object? sender, SKBitmap frame)
    {
        // FrameProcessor handles its own scaling (max 720px).
        // Image control handles final display sizing via Stretch="UniformToFill".
        this.frameProcessor?.ProcessPreviewFrame(frame, 720, 720);
    }

    private void OnFrameReady(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        if (this.previewImage != null)
        {
            this.previewImage.Source = bitmap;
        }
    }

    private void OnFpsUpdated(string fps)
    {
        if (this.fpsTextBlock != null && this.DebugMode)
        {
            this.fpsTextBlock.Text = fps;
        }
    }

    private void OnPhotoCaptured(object? sender, byte[] photoData)
    {
        this.IsBusying = false;
        this.SetValue(IsCapturingNextFrameProperty, false);

        var result = new PhotoCaptureResult(true, photoData, null);
        this.PhotoCaptured?.Invoke(this, photoData);

        var cmd = this.PhotoCapturedCommand;
        if (cmd?.CanExecute(result) == true)
            cmd.Execute(result);
    }

    private void OnCameraError(object? sender, string error)
    {
        this.CameraError?.Invoke(this, error);

        // Reset capture trigger on photo errors too
        if (error.Contains("Photo", StringComparison.OrdinalIgnoreCase))
        {
            this.IsBusying = false;
            this.SetValue(IsCapturingNextFrameProperty, false);

            var result = new PhotoCaptureResult(false, null, error);
            var cmd = this.PhotoCapturedCommand;
            if (cmd?.CanExecute(result) == true)
                cmd.Execute(result);
        }
    }

    // --- Property Changes ---

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CameraEnabledProperty && this.cameraProvider != null)
        {
            if (change.GetNewValue<bool>())
                _ = this.cameraProvider.StartPreviewAsync();
            else
                _ = this.cameraProvider.StopPreviewAsync();
        }
        else if (change.Property == TorchOnProperty)
        {
            _ = this.cameraProvider?.SetTorchAsync(change.GetNewValue<bool>());
        }
        else if (change.Property == RequestZoomFactorProperty)
        {
            if (change.GetNewValue<float?>() is float z)
                _ = this.cameraProvider?.SetZoomAsync(z);
        }
        else if (change.Property == CameraFacingProperty)
        {
            var facing = change.GetNewValue<CameraFacing>();
            _ = this.cameraProvider?.SwitchCameraAsync(facing);
        }
        else if (change.Property == FlashModeProperty)
        {
            _ = this.cameraProvider?.SetFlashModeAsync(change.GetNewValue<FlashMode>());
        }
        else if (change.Property == PhotoResolutionProperty)
        {
            if (change.GetNewValue<PhotoResolution?>() is PhotoResolution res)
                _ = this.cameraProvider?.SetPhotoResolutionAsync(res);
        }
        else if (change.Property == IsCapturingNextFrameProperty)
        {
            if (change.GetNewValue<bool>())
                _ = this.TakePhotoAsync();
        }
    }

    // --- Gesture Handling ---

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        this.activePointers[e.Pointer.Id] = point;

        if (this.activePointers.Count == 1)
        {
            this.potentialTapPoint = point;
            this.potentialTapTime = DateTime.Now;
            this.isPinching = false;
        }
        else if (this.activePointers.Count == 2)
        {
            // Pinch started — record initial distance and current zoom
            var pts = this.activePointers.Values.ToArray();
            this.pinchStartDistance = Math.Sqrt(
                Math.Pow(pts[0].X - pts[1].X, 2) +
                Math.Pow(pts[0].Y - pts[1].Y, 2));
            this.pinchStartZoom = this.cameraProvider?.CurrentZoomFactor ?? 1f;
            this.isPinching = true;
        }

        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!this.PinchToZoomEnabled) return;

        if (this.activePointers.TryGetValue(e.Pointer.Id, out _))
        {
            this.activePointers[e.Pointer.Id] = e.GetPosition(this);
        }

        if (this.activePointers.Count != 2) return;

        var pts = this.activePointers.Values.ToArray();
        var dist = Math.Sqrt(
            Math.Pow(pts[0].X - pts[1].X, 2) +
            Math.Pow(pts[0].Y - pts[1].Y, 2));

        if (this.isPinching && this.pinchStartDistance > 0)
        {
            // Relative scale based on initial pinch distance
            float scale = (float)(dist / this.pinchStartDistance);
            float newZoom = this.pinchStartZoom * scale;
            newZoom = Math.Clamp(newZoom,
                this.cameraProvider?.MinZoomFactor ?? 1f,
                this.cameraProvider?.MaxZoomFactor ?? 5f);
            _ = this.cameraProvider?.SetZoomAsync(newZoom);
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        this.activePointers.Remove(e.Pointer.Id);

        if (!this.isPinching && this.TapToFocusEnabled &&
            this.activePointers.Count == 0)
        {
            var point = e.GetPosition(this);
            var elapsed = DateTime.Now - this.potentialTapTime;
            var distance = Math.Sqrt(
                Math.Pow(point.X - this.potentialTapPoint.X, 2) +
                Math.Pow(point.Y - this.potentialTapPoint.Y, 2));

            if (elapsed.TotalMilliseconds < 300 && distance < 10)
            {
                float nx = (float)(point.X / Math.Max(this.Bounds.Width, 1));
                float ny = (float)(point.Y / Math.Max(this.Bounds.Height, 1));
                this.ShowFocusAnimation(point);
                _ = this.cameraProvider?.SetFocusAsync(nx, ny);
            }
        }

        e.Handled = true;
    }

    // --- Focus Animation ---

    private async void ShowFocusAnimation(Point point)
    {
        if (this.focusIndicator == null) return;

        this.focusIndicator.IsVisible = true;
        this.focusIndicator.Opacity = 1.0;
        Canvas.SetLeft(this.focusIndicator, point.X - 40);
        Canvas.SetTop(this.focusIndicator, point.Y - 40);

        // Phase 1: Scale in 1.5 → 1.0 (150ms)
        for (int i = 0; i < 10; i++)
        {
            double scale = 1.5 - (0.5 * i / 10.0);
            this.focusIndicator.RenderTransform = new ScaleTransform(scale, scale);
            await Task.Delay(15);
        }
        this.focusIndicator.RenderTransform = new ScaleTransform(1.0, 1.0);

        // Phase 2: Pulse 2 times (400ms)
        for (int pulse = 0; pulse < 2; pulse++)
        {
            for (int i = 0; i < 10; i++)
            {
                double scale = 1.0 + (0.15 * Math.Sin(i * Math.PI / 10.0));
                this.focusIndicator.RenderTransform = new ScaleTransform(scale, scale);
                await Task.Delay(20);
            }
        }
        this.focusIndicator.RenderTransform = new ScaleTransform(1.0, 1.0);

        // Phase 3: Fade out (200ms)
        for (int i = 0; i < 10; i++)
        {
            this.focusIndicator.Opacity = 1.0 - (i / 10.0);
            await Task.Delay(20);
        }
        this.focusIndicator.Opacity = 0.0;

        this.focusIndicator.IsVisible = false;
    }

    // --- Cleanup ---

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (this.cameraProvider != null)
        {
            this.cameraProvider.FrameReceived -= this.OnFrameReceived;
            this.cameraProvider.PhotoCaptured -= this.OnPhotoCaptured;
            this.cameraProvider.ErrorOccurred -= this.OnCameraError;
            this.cameraProvider.Dispose();
        }
    }
}
