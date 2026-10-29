using System.IO;
using System.Windows;

namespace DriverScout;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Режим без окна: DriverScout.exe --report отчёт.txt — сканирует и пишет отчёт в файл.
        if (e.Args.Length >= 2 && e.Args[0] == "--report")
        {
            var scan = new ScanService();
            // В отдельном потоке: внутри идут синхронные ожидания сетевых запросов.
            var rows = Task.Run(() => scan.Run(_ => { })).GetAwaiter().GetResult();
            File.WriteAllText(e.Args[1], ScanService.BuildReport(rows, scan.SourceError, scan.Oem));
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }
}
