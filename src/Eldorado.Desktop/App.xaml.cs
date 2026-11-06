using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace Eldorado.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        bool preview = e.Args.Length > 0 && e.Args[0] == "--preview";
        if (preview)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string output = e.Args.Length > 1 ? e.Args[1] : "preview.png";
            try
            {
                if (e.Args.Length != 2) throw new ArgumentException("Usage: --preview <output.png>");
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                base.OnStartup(e);
                var window = new MainWindow(previewMode: true);
                MainWindow = window;
                await PreviewRenderer.RenderAsync(window, output);
                Shutdown(0);
            }
            catch (Exception error)
            {
                try
                {
                    string errorPath = Path.GetFullPath(output) + ".error.txt";
                    Directory.CreateDirectory(Path.GetDirectoryName(errorPath)!);
                    File.WriteAllText(errorPath, error.ToString());
                }
                catch (Exception writeError)
                {
                    Console.Error.WriteLine(writeError.Message);
                }
                Shutdown(1);
            }
            return;
        }
        instance = new Mutex(true, "Local\\EldoradoShop.Desktop", out bool firstInstance);
        if (!firstInstance)
        {
            System.Windows.MessageBox.Show("Eldorado đang chạy. Mở ứng dụng từ biểu tượng ở khay hệ thống.", "Eldorado");
            Shutdown();
            return;
        }
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
