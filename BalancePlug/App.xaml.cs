using System;
using System.Threading;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Security.Principal;

namespace BalancePlug;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var directory = Environment.GetEnvironmentVariable("BALANCEPLUG_DATA_DIR");
        var suffix = directory is null ? "" : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())));
        instance = new Mutex(true, @"Local\BalancePlug." + sid + suffix, out var first);
        if (!first) { instance.Dispose(); instance = null; Shutdown(); return; }
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        instance?.ReleaseMutex(); instance?.Dispose();
        base.OnExit(e);
    }
}
