using System;
using Avalonia.Controls;
using Avalonia.Threading;
using CameraView.Demo.ViewModels;

namespace CameraView.Demo.Views;

public partial class MainView : UserControl
{
    private IDisposable? orientationTimer;

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

        // 定时轮询朝向数据（OneWayToSource 属性不能从 UI 端绑定）
        orientationTimer = DispatcherTimer.Run(() =>
        {
            if (this.DataContext is MainViewModel vm)
                vm.UpdateOrientation(this.CameraControl);
            return true;
        }, TimeSpan.FromMilliseconds(300));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        orientationTimer?.Dispose();
        orientationTimer = null;
    }
}
