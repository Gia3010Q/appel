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
        window.ShowInTaskbar = true;
        window.Show();
        window.WindowState = WindowState.Minimized;
        if (!window.IsVisible || !window.ShowInTaskbar)
            throw new InvalidOperationException("Minimizing must preserve the taskbar window.");
        window.WindowState = WindowState.Normal;
        window.Close();
        if (!window.IsVisible || !window.ShowInTaskbar || window.WindowState != WindowState.Minimized)
            throw new InvalidOperationException("Closing must minimize to the taskbar by default.");
        window.WindowState = WindowState.Normal;
        window.ShowInTaskbar = false;
        window.LoadPreviewData();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var content = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("Preview requires a WPF content element.");
        window.Content = null;
        content.Resources.MergedDictionaries.Add(window.Resources);
        System.Windows.Documents.TextElement.SetForeground(content, window.Foreground);
        content.Width = 1440;
        content.Height = 900;
        var surface = new System.Windows.Controls.Border
        {
            Width = 1440, Height = 900,
            Background = new SolidColorBrush(Color.FromRgb(11, 17, 28)),
            Child = content
        };
        surface.Measure(new Size(1440, 900));
        surface.Arrange(new Rect(0, 0, 1440, 900));
        surface.UpdateLayout();
        await window.Dispatcher.InvokeAsync(surface.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var file = File.Create(destination);
        encoder.Save(file);
    }
}
