using System;
using System.Threading;
using System.Windows;

namespace Eldorado.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, "Local\\EldoradoShop.Desktop", out bool firstInstance);
        if (!firstInstance)
        {
            System.Windows.MessageBox.Show("Eldorado đang chạy. Mở ứng dụng từ biểu tượng ở khay hệ thống.", "Eldorado");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
