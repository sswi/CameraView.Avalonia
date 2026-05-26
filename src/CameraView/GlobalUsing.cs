// 共享命名空间
global using Avalonia;
global using Avalonia.Controls;
global using Avalonia.Controls.Primitives;
global using Avalonia.Controls.Shapes;
global using Avalonia.Data;
global using Avalonia.Input;
global using Avalonia.Interactivity;
global using Avalonia.Media;
global using System.Windows.Input;
global using CameraView.Models;
global using CameraView.Services;
global using SkiaSharp;
global using Avalonia.Threading;
global using Avalonia.Media.Imaging;
global using Avalonia.Platform;

// Android 平台命名空间
#if ANDROID
global using Android;
global using Android.Content;
global using Android.Content.PM;
global using Android.Runtime;
global using AndroidX.Camera.Lifecycle;
global using AndroidX.Core.App;
global using AndroidX.Core.Content;
global using AndroidX.Lifecycle;
global using CameraView.Platforms.Android;
#endif