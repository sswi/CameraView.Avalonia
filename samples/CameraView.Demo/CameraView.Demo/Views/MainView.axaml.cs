using System;
using Avalonia.Controls;
using CameraView.Demo.ViewModels;

namespace CameraView.Demo.Views;

public partial class MainView : UserControl
{
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
}
