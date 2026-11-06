using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Eldorado.Desktop;

internal static class PreviewRenderer
{
    public static async Task RenderAsync(MainWindow window, string outputPath)
    {
        window.Width = 1440;
        window.Height = 900;
        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.LoadPreviewData();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var content = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("Preview requires a WPF content element.");
        content.Measure(new Size(1440, 900));
        content.Arrange(new Rect(0, 0, 1440, 900));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var file = File.Create(destination);
        encoder.Save(file);
    }
}
