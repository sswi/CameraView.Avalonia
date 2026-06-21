namespace CameraView;

/// <summary>
/// 相机预览控件 — Avalonia 跨平台相机视图
/// 提供预览、拍照、对焦、缩放、曝光、设备朝向等功能
/// </summary>
public class CameraViewControl : TemplatedControl
{
    // ========== 模板部件 ==========
    private Image? previewImage;
    private Ellipse? focusIndicator;
    private Canvas? focusCanvas;
    private TextBlock? fpsTextBlock;

    // ========== 手势状态 ==========
    private readonly Dictionary<long, Point> activePointers = [];  // 活跃触摸点
    private Point potentialTapPoint;                                // 潜在点击起始点
    private DateTime potentialTapTime;                              // 潜在点击时间
    private bool isPinching;                                        // 是否正在捏合
    private double pinchStartDistance;                              // 捏合起始距离
    private float pinchStartZoom = 1f;                              // 捏合起始缩放

    // ========== 服务实例 ==========
    private ICameraProvider? cameraProvider;
    private IDeviceOrientationProvider? orientationProvider;
    private readonly FrameProcessor? frameProcessor;
    private DateTime lastOrientationUpdate;
    private bool isFocusing;

    // ========================================================================
    //  可绑定属性
    // ========================================================================

    /// <summary>开关相机 (TwoWay)</summary>
    public static readonly StyledProperty<bool> CameraEnabledProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(CameraEnabled), false);
    public bool CameraEnabled { get => GetValue(CameraEnabledProperty); set => SetValue(CameraEnabledProperty, value); }

    /// <summary>前后摄像头 (TwoWay)</summary>
    public static readonly StyledProperty<CameraFacing> CameraFacingProperty =
        AvaloniaProperty.Register<CameraViewControl, CameraFacing>(nameof(CameraFacing), CameraFacing.Back);
    public CameraFacing CameraFacing { get => GetValue(CameraFacingProperty); set => SetValue(CameraFacingProperty, value); }

    /// <summary>是否为前置摄像头：true=前置, false=后置。单摄像头时设置无效 (TwoWay)</summary>
    public static readonly StyledProperty<bool> IsFrontCameraProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(IsFrontCamera), false, defaultBindingMode: BindingMode.TwoWay);
    public bool IsFrontCamera
    {
        get => GetValue(IsFrontCameraProperty);
        set => SetValue(IsFrontCameraProperty, value);
    }

    /// <summary>手电筒 (TwoWay)</summary>
    public static readonly StyledProperty<bool> TorchOnProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(TorchOn), false);
    public bool TorchOn { get => GetValue(TorchOnProperty); set => SetValue(TorchOnProperty, value); }

    /// <summary>请求缩放倍率 (TwoWay)</summary>
    public static readonly StyledProperty<float?> RequestZoomFactorProperty =
        AvaloniaProperty.Register<CameraViewControl, float?>(nameof(RequestZoomFactor));
    public float? RequestZoomFactor { get => GetValue(RequestZoomFactorProperty); set => SetValue(RequestZoomFactorProperty, value); }

    /// <summary>当前缩放倍率 (只读)</summary>
    public static readonly StyledProperty<float?> CurrentZoomFactorProperty =
        AvaloniaProperty.Register<CameraViewControl, float?>(nameof(CurrentZoomFactor));
    public float? CurrentZoomFactor { get => GetValue(CurrentZoomFactorProperty); set => SetValue(CurrentZoomFactorProperty, value); }

    /// <summary>点击对焦开关 (TwoWay)</summary>
    public static readonly StyledProperty<bool> TapToFocusEnabledProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(TapToFocusEnabled), true);
    public bool TapToFocusEnabled { get => GetValue(TapToFocusEnabledProperty); set => SetValue(TapToFocusEnabledProperty, value); }

    /// <summary>对焦圆环颜色 (TwoWay)</summary>
    public static readonly StyledProperty<IBrush> FocusIndicatorStrokeProperty =
        AvaloniaProperty.Register<CameraViewControl, IBrush>(nameof(FocusIndicatorStroke), Brushes.DeepPink);
    public IBrush FocusIndicatorStroke { get => GetValue(FocusIndicatorStrokeProperty); set => SetValue(FocusIndicatorStrokeProperty, value); }

    /// <summary>对焦圆环粗细 (TwoWay)</summary>
    public static readonly StyledProperty<double> FocusIndicatorStrokeThicknessProperty =
        AvaloniaProperty.Register<CameraViewControl, double>(nameof(FocusIndicatorStrokeThickness), 2.0);
    public double FocusIndicatorStrokeThickness { get => GetValue(FocusIndicatorStrokeThicknessProperty); set => SetValue(FocusIndicatorStrokeThicknessProperty, value); }

    /// <summary>捏合缩放开关 (TwoWay)</summary>
    public static readonly StyledProperty<bool> PinchToZoomEnabledProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(PinchToZoomEnabled), true);
    public bool PinchToZoomEnabled { get => GetValue(PinchToZoomEnabledProperty); set => SetValue(PinchToZoomEnabledProperty, value); }

    /// <summary>触发拍照信号：设为 true 触发拍照，完成后自动重置为 false (TwoWay)</summary>
    public static readonly StyledProperty<bool> IsCapturingNextFrameProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(IsCapturingNextFrame), false,
            defaultBindingMode: BindingMode.TwoWay);
    public bool IsCapturingNextFrame { get => GetValue(IsCapturingNextFrameProperty); set => SetValue(IsCapturingNextFrameProperty, value); }

    /// <summary>忙碌状态：拍照/切换相机时为 true，完成后自动恢复 (OneWayToSource)</summary>
    public static readonly StyledProperty<bool> IsBusyingProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(IsBusying), false,
            defaultBindingMode: BindingMode.OneWayToSource);
    public bool IsBusying { get => GetValue(IsBusyingProperty); private set => SetValue(IsBusyingProperty, value); }

    /// <summary>外部注入的相机提供者</summary>
    public static readonly StyledProperty<ICameraProvider?> CameraProviderProperty =
        AvaloniaProperty.Register<CameraViewControl, ICameraProvider?>(nameof(CameraProvider));
    public ICameraProvider? CameraProvider { get => GetValue(CameraProviderProperty); set => SetValue(CameraProviderProperty, value); }

    /// <summary>拍照完成命令（参数为 PhotoCaptureResult）</summary>
    public static readonly StyledProperty<ICommand?> PhotoCapturedCommandProperty =
        AvaloniaProperty.Register<CameraViewControl, ICommand?>(nameof(PhotoCapturedCommand));
    public ICommand? PhotoCapturedCommand { get => GetValue(PhotoCapturedCommandProperty); set => SetValue(PhotoCapturedCommandProperty, value); }

    /// <summary>相机错误命令（参数为 string 错误消息）</summary>
    public static readonly StyledProperty<ICommand?> ErrorCommandProperty =
        AvaloniaProperty.Register<CameraViewControl, ICommand?>(nameof(ErrorCommand));
    public ICommand? ErrorCommand { get => GetValue(ErrorCommandProperty); set => SetValue(ErrorCommandProperty, value); }

    /// <summary>调试模式：显示 FPS 叠加层 (TwoWay)</summary>
    public static readonly StyledProperty<bool> DebugModeProperty =
        AvaloniaProperty.Register<CameraViewControl, bool>(nameof(DebugMode), false);
    public bool DebugMode { get => GetValue(DebugModeProperty); set => SetValue(DebugModeProperty, value); }

    /// <summary>闪光灯模式 (TwoWay)</summary>
    public static readonly StyledProperty<FlashMode> FlashModeProperty =
        AvaloniaProperty.Register<CameraViewControl, FlashMode>(nameof(FlashMode), Models.FlashMode.Auto);
    public FlashMode FlashMode { get => GetValue(FlashModeProperty); set => SetValue(FlashModeProperty, value); }

    /// <summary>照片分辨率 (TwoWay)</summary>
    public static readonly StyledProperty<PhotoResolution?> PhotoResolutionProperty =
        AvaloniaProperty.Register<CameraViewControl, PhotoResolution?>(nameof(PhotoResolution));
    public PhotoResolution? PhotoResolution { get => GetValue(PhotoResolutionProperty); set => SetValue(PhotoResolutionProperty, value); }

    /// <summary>支持的照片分辨率列表 (只读)</summary>
    public static readonly DirectProperty<CameraViewControl, IReadOnlyList<PhotoResolution>> SupportedResolutionsProperty =
        AvaloniaProperty.RegisterDirect<CameraViewControl, IReadOnlyList<PhotoResolution>>(
            nameof(SupportedResolutions), o => o.SupportedResolutions);
    public IReadOnlyList<PhotoResolution> SupportedResolutions => cameraProvider?.SupportedPhotoResolutions ?? [];

    /// <summary>曝光补偿 EV (TwoWay)</summary>
    public static readonly StyledProperty<float> ExposureCompensationProperty =
        AvaloniaProperty.Register<CameraViewControl, float>(nameof(ExposureCompensation));
    public float ExposureCompensation { get => this.GetValue(ExposureCompensationProperty); set => this.SetValue(ExposureCompensationProperty, value); }

    /// <summary>设备朝向原始数据 (OneWayToSource)</summary>
    public static readonly StyledProperty<DeviceOrientation?> DeviceOrientationProperty =
        AvaloniaProperty.Register<CameraViewControl, DeviceOrientation?>(nameof(DeviceOrientation), null,
            defaultBindingMode: BindingMode.OneWayToSource);
    public DeviceOrientation? DeviceOrientation
    {
        get => GetValue(DeviceOrientationProperty);
        private set { SetValue(DeviceOrientationProperty, value); if (value != null) OrientationState = value.State; }
    }

    /// <summary>设备朝向状态（人可读） (OneWayToSource)</summary>
    public static readonly StyledProperty<DeviceOrientationState> OrientationStateProperty =
        AvaloniaProperty.Register<CameraViewControl, DeviceOrientationState>(nameof(OrientationState),
            DeviceOrientationState.PortraitUpright, defaultBindingMode: BindingMode.OneWayToSource);
    public DeviceOrientationState OrientationState { get => GetValue(OrientationStateProperty); private set => SetValue(OrientationStateProperty, value); }

    // ========================================================================
    //  事件
    // ========================================================================

    /// <summary>拍照完成事件（JPEG 原始字节）</summary>
    public event EventHandler<byte[]>? PhotoCaptured;
    /// <summary>相机错误事件</summary>
    public event EventHandler<string>? CameraError;
    /// <summary>设备朝向变化事件</summary>
    public event EventHandler<DeviceOrientation>? DeviceOrientationChanged;

    // ========================================================================
    //  构造 & 模板绑定
    // ========================================================================

    public CameraViewControl()
    {
        frameProcessor = new FrameProcessor();
        frameProcessor.FrameReady += OnFrameReady;
        frameProcessor.FpsUpdated += OnFpsUpdated;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        previewImage = e.NameScope.Find<Image>("PART_PreviewImage");
        focusIndicator = e.NameScope.Find<Ellipse>("PART_FocusIndicator");
        focusCanvas = e.NameScope.Find<Canvas>("PART_FocusCanvas");
        fpsTextBlock = e.NameScope.Find<TextBlock>("PART_FpsText");

        // 注册手势事件
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);

        RunInitializationAsync();
    }

    private async void RunInitializationAsync()
    {
        if (CameraProvider != null)
            await InitializeCameraAsync(CameraProvider);
    }

    // ========================================================================
    //  公共 API
    // ========================================================================

    /// <summary>初始化相机：注册事件并调用平台初始化</summary>
    public async Task InitializeCameraAsync(ICameraProvider provider)
    {
        cameraProvider = provider;
        cameraProvider.FrameReceived += OnFrameReceived;
        cameraProvider.PhotoCaptured += OnPhotoCaptured;
        cameraProvider.ErrorOccurred += OnCameraError;

        await cameraProvider.InitializeAsync(new CameraOptions { CameraFacing = CameraFacing });
    }

    /// <summary>启动相机预览 + 设备朝向传感器。
    /// 若尚未初始化，自动调用 InitializeCameraAsync。</summary>
    public async Task StartCameraAsync()
    {
        if (cameraProvider == null)
        {
            var provider = CameraProvider ?? CameraProviderFactory.Create();
            if (provider == null) return;
            await InitializeCameraAsync(provider);
        }

        IsBusying = true;
        await cameraProvider!.StartPreviewAsync();
        IsBusying = false;
        EnsureOrientationProvider();
        orientationProvider?.Start();
    }

    /// <summary>停止预览 + 传感器</summary>
    public async Task StopCameraAsync()
    {
        if (cameraProvider != null)
        {
            orientationProvider?.Stop();
            IsBusying = true;
            await cameraProvider.StopPreviewAsync();
            IsBusying = false;
        }
    }

    private void EnsureOrientationProvider()
    {
        if (orientationProvider != null) return;
        orientationProvider = CameraProviderFactory.CreateOrientationProvider();
        orientationProvider?.OrientationChanged += o =>
            {
                var now = DateTime.Now;
                if ((now - lastOrientationUpdate).TotalMilliseconds > 100)
                {
                    lastOrientationUpdate = now;
                    DeviceOrientation = o;
                    DeviceOrientationChanged?.Invoke(this, o);
                    // 推送方向给 provider 用于照片旋转校正（iOS/Android）
                    if (cameraProvider is ICameraOrientationAware aware)
                        aware.UpdateDeviceOrientation(o.State);
                }
            };
    }

    /// <summary>拍照（异步触发，结果通过 PhotoCaptured 事件/命令返回）</summary>
    public async Task TakePhotoAsync()
    {
        if (cameraProvider != null)
        {
            IsBusying = true;
            await cameraProvider.TakePhotoAsync();
        }
    }

    /// <summary>切换前/后摄像头</summary>
    public async Task SwitchCameraAsync()
    {
        if (cameraProvider != null)
        {
            IsBusying = true;
            var newFacing = cameraProvider.CurrentFacing == CameraFacing.Back ? CameraFacing.Front : CameraFacing.Back;
            await cameraProvider.SwitchCameraAsync(newFacing);
            IsBusying = false;
        }
    }

    // ========================================================================
    //  帧处理
    // ========================================================================

    /// <summary>收到相机原始帧 → 交给 FrameProcessor 异步处理</summary>
    private void OnFrameReceived(object? sender, SKBitmap frame)
    {
        frameProcessor?.ProcessPreviewFrame(frame);
    }

    /// <summary>FrameProcessor 处理好帧 → 更新 UI</summary>
    private void OnFrameReady(Bitmap bitmap)
    {
        previewImage?.Source = bitmap;
    }

    /// <summary>FPS 更新 → 仅 DebugMode 时显示</summary>
    private void OnFpsUpdated(string fps)
    {
        if (fpsTextBlock != null && DebugMode) fpsTextBlock.Text = fps;
    }

    /// <summary>拍照成功回调：解除忙碌/触发信号、触发事件和命令</summary>
    private void OnPhotoCaptured(object? sender, byte[] photoData)
    {
        IsBusying = false;
        SetValue(IsCapturingNextFrameProperty, false);

        var result = new PhotoCaptureResult(true, photoData, null);
        PhotoCaptured?.Invoke(this, photoData);

        var cmd = PhotoCapturedCommand;
        if (cmd?.CanExecute(result) == true) cmd.Execute(result);
    }

    /// <summary>错误回调：触发事件 + 执行 ErrorCommand + 拍照错误时解除忙碌</summary>
    private void OnCameraError(object? sender, string error)
    {
        CameraError?.Invoke(this, error);

        var errCmd = ErrorCommand;
        if (errCmd?.CanExecute(error) == true)
            errCmd.Execute(error);

        if (error.Contains("Photo", StringComparison.OrdinalIgnoreCase))
        {
            IsBusying = false;
            SetValue(IsCapturingNextFrameProperty, false);

            var result = new PhotoCaptureResult(false, null, error);
            var cmd = PhotoCapturedCommand;
            if (cmd?.CanExecute(result) == true) cmd.Execute(result);
        }
    }

    // ========================================================================
    //  属性变更 → 平台调用
    // ========================================================================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CameraEnabledProperty)
        {
            if (change.GetNewValue<bool>())
                _ = StartCameraAsync();
            else
                _ = StopCameraAsync();
        }
        else if (change.Property == TorchOnProperty)
            _ = cameraProvider?.SetTorchAsync(change.GetNewValue<bool>());
        else if (change.Property == RequestZoomFactorProperty)
        { if (change.GetNewValue<float?>() is float z) _ = cameraProvider?.SetZoomAsync(z); }
        else if (change.Property == CameraFacingProperty)
            _ = cameraProvider?.SwitchCameraAsync(change.GetNewValue<CameraFacing>());
        else if (change.Property == IsFrontCameraProperty)
        {
            var target = change.GetNewValue<bool>() ? CameraFacing.Front : CameraFacing.Back;
            _ = cameraProvider?.SwitchCameraAsync(target);
        }
        else if (change.Property == FlashModeProperty)
            _ = cameraProvider?.SetFlashModeAsync(change.GetNewValue<FlashMode>());
        else if (change.Property == PhotoResolutionProperty)
        { if (change.GetNewValue<PhotoResolution?>() is PhotoResolution res) _ = cameraProvider?.SetPhotoResolutionAsync(res); }
        else if (change.Property == ExposureCompensationProperty)
            _ = cameraProvider?.SetExposureCompensationAsync(change.GetNewValue<float>());
        else if (change.Property == IsCapturingNextFrameProperty)
        { if (change.GetNewValue<bool>()) _ = TakePhotoAsync(); }
    }

    // ========================================================================
    //  手势处理
    // ========================================================================

    /// <summary>手指按下：单指记录潜在点击位置，双指记录捏合起始距离</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        activePointers[e.Pointer.Id] = point;

        if (activePointers.Count == 1)
        {
            potentialTapPoint = point;
            potentialTapTime = DateTime.Now;
            isPinching = false;
        }
        else if (activePointers.Count == 2)
        {
            var pts = activePointers.Values.ToArray();
            pinchStartDistance = Math.Sqrt(Math.Pow(pts[0].X - pts[1].X, 2) + Math.Pow(pts[0].Y - pts[1].Y, 2));
            pinchStartZoom = cameraProvider?.CurrentZoomFactor ?? 1f;
            isPinching = true;
        }

        e.Handled = true;
    }

    /// <summary>手指移动：捏合时计算相对缩放比例</summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!PinchToZoomEnabled) return;

        if (activePointers.TryGetValue(e.Pointer.Id, out _))
            activePointers[e.Pointer.Id] = e.GetPosition(this);

        if (activePointers.Count != 2) return;

        var pts = activePointers.Values.ToArray();
        var dist = Math.Sqrt(Math.Pow(pts[0].X - pts[1].X, 2) + Math.Pow(pts[0].Y - pts[1].Y, 2));

        if (isPinching && pinchStartDistance > 0)
        {
            float scale = (float)(dist / pinchStartDistance);
            float newZoom = Math.Clamp(pinchStartZoom * scale, cameraProvider?.MinZoomFactor ?? 1f, cameraProvider?.MaxZoomFactor ?? 5f);
            _ = cameraProvider?.SetZoomAsync(newZoom);
        }

        e.Handled = true;
    }

    /// <summary>手指抬起：判定是否为点击（300ms + 10px 移动）→ 对焦</summary>
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        activePointers.Remove(e.Pointer.Id);

        if (!isPinching && TapToFocusEnabled && activePointers.Count == 0)
        {
            var point = e.GetPosition(this);
            var elapsed = DateTime.Now - potentialTapTime;
            var distance = Math.Sqrt(Math.Pow(point.X - potentialTapPoint.X, 2) + Math.Pow(point.Y - potentialTapPoint.Y, 2));

            if (elapsed.TotalMilliseconds < 300 && distance < 10 && !isFocusing)
            {
                float nx = (float)(point.X / Math.Max(Bounds.Width, 1));
                float ny = (float)(point.Y / Math.Max(Bounds.Height, 1));
                _ = ShowFocusAnimationAsync(point);
                _ = cameraProvider?.SetFocusAsync(nx, ny);
            }
        }

        e.Handled = true;
    }

    // ========================================================================
    //  对焦动画（缩小入场 → 呼吸等待 → 淡出，对焦期间阻止重复点击）
    // ========================================================================

    private async Task ShowFocusAnimationAsync(Point point)
    {
        if (focusIndicator == null) return;

        isFocusing = true;
        focusIndicator.IsVisible = true;
        focusIndicator.Opacity = 1.0;
        Canvas.SetLeft(focusIndicator, point.X - 40);
        Canvas.SetTop(focusIndicator, point.Y - 40);

        // 阶段 1：缩小入场 1.5 → 1.0 (150ms, 60fps)
        for (int i = 0; i < 9; i++)
        {
            double scale = 1.5 - (0.5 * i / 9.0);
            focusIndicator.RenderTransform = new ScaleTransform(scale, scale);
            await Task.Delay(16);
        }
        focusIndicator.RenderTransform = new ScaleTransform(1.0, 1.0);

        // 阶段 2：等待对焦完成（~2s，60fps 呼吸）
        for (int p = 0; p < 80; p++)
        {
            double t = p * Math.PI / 30.0;
            double scale = 1.0 + 0.06 * Math.Sin(t);
            focusIndicator.RenderTransform = new ScaleTransform(scale, scale);
            await Task.Delay(16);
        }
        focusIndicator.RenderTransform = new ScaleTransform(1.0, 1.0);

        // 阶段 3：淡出 (200ms, 60fps)
        for (int i = 0; i < 12; i++)
        {
            focusIndicator.Opacity = 1.0 - (i / 12.0);
            await Task.Delay(16);
        }
        focusIndicator.Opacity = 0.0;
        focusIndicator.IsVisible = false;
        isFocusing = false;
    }

    // ========================================================================
    //  清理
    // ========================================================================

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (cameraProvider != null)
        {
            cameraProvider.FrameReceived -= OnFrameReceived;
            cameraProvider.PhotoCaptured -= OnPhotoCaptured;
            cameraProvider.ErrorOccurred -= OnCameraError;
            cameraProvider.Dispose();
        }
    }
}
