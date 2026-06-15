using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using CameraView.Demo.ViewModels;
using Avalonia.Input.Platform;

namespace CameraView.Demo.Views;

public partial class MainView : UserControl
{
    private DispatcherTimer? orientationTimer;

    public MainView()
    {
        InitializeComponent();
        RootGrid.SizeChanged += OnRootGridSizeChanged;
        // 使用 FindControl 获取 XAML 中命名的控件，避免生成字段未同步的问题
        var openBtn = this.FindControl<Avalonia.Controls.Button>("OpenStatusButton");
        if (openBtn != null)
        {
            openBtn.Click += (_, _) =>
            {
                if (this.DataContext is MainViewModel vm)
                {
                    var overlay = this.FindControl<Border>("StatusOverlay");
                    var box = this.FindControl<Avalonia.Controls.TextBox>("OverlayStatusBox");
                    if (box != null) box.Text = vm.StatusText;
                    if (overlay != null) overlay.IsVisible = true;
                }
            };
        }

        // Overlay 控件事件
        var copyBtn = this.FindControl<Avalonia.Controls.Button>("OverlayCopyButton");
        var closeBtn = this.FindControl<Avalonia.Controls.Button>("OverlayCloseButton");
        if (copyBtn != null)
        {
            copyBtn.Click += async (_, _) =>
            {
                var box = this.FindControl<Avalonia.Controls.TextBox>("OverlayStatusBox");
                if (box == null) return;

                try
                {
#if IOS
                    UIKit.UIPasteboard.General.String = box.Text ?? string.Empty;
#else
                    // 通过反射在运行时访问 Avalonia.Application.Current.Clipboard，避免编译期 API 不存在问题
                    var app = Avalonia.Application.Current;
                    if (app != null)
                    {
                        var prop = app.GetType().GetProperty("Clipboard");
                        if (prop != null)
                        {
                            var clipboard = prop.GetValue(app);
                            if (clipboard != null)
                            {
                                var setTextAsync = clipboard.GetType().GetMethod("SetTextAsync", new[] { typeof(string) });
                                if (setTextAsync != null)
                                {
                                    var task = (System.Threading.Tasks.Task)setTextAsync.Invoke(clipboard, new object[] { box.Text ?? string.Empty });
                                    if (task != null)
                                        await task.ConfigureAwait(false);
                                }
                            }
                        }
                    }
#endif
                }
                catch { }
            };
        }

        if (closeBtn != null)
        {
            closeBtn.Click += (_, _) =>
            {
                var overlay = this.FindControl<Border>("StatusOverlay");
                if (overlay != null) overlay.IsVisible = false;
            };
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.DataContext is MainViewModel vm)
        {
            vm.CameraControl = this.CameraControl;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        this.orientationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        this.orientationTimer.Tick += (_, _) =>
        {
            if (this.DataContext is MainViewModel vm)
                vm.UpdateOrientation(this.CameraControl);
        };
        this.orientationTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        orientationTimer?.Stop();
        orientationTimer = null;
    }

    /// <summary>响应式布局：宽>高=左右，高>宽=上下</summary>
    private void OnRootGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var isLandscape = e.NewSize.Width > e.NewSize.Height;

        if (isLandscape)
        {
            // 左画面 + 右设置
            RootGrid.ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            ];
            RootGrid.RowDefinitions.Clear();

            Grid.SetRow(CameraControl, 0);
            Grid.SetColumn(CameraControl, 0);
            CameraControl.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;

            Grid.SetRow(SettingsPanel, 0);
            Grid.SetColumn(SettingsPanel, 1);
        }
        else
        {
            // 上画面 + 下设置
            RootGrid.RowDefinitions =
            [
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            ];
            RootGrid.ColumnDefinitions.Clear();

            Grid.SetRow(CameraControl, 0);
            Grid.SetColumn(CameraControl, 0);

            Grid.SetRow(SettingsPanel, 1);
            Grid.SetColumn(SettingsPanel, 0);
        }
    }
}
