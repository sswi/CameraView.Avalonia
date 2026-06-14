using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CameraView.Demo.ViewModels;

namespace CameraView.Demo.Views;

public partial class MainView : UserControl
{
    private DispatcherTimer? orientationTimer;

    public MainView()
    {
        InitializeComponent();
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

        // 定时轮询朝向数据（OneWayToSource 属性不能在 XAML 端绑定）
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
}
