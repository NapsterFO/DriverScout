using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DriverScout;

/// <summary>Откуда ставится программа.</summary>
public enum AppSource
{
    Winget,  // winget install --id … — официальный репозиторий Microsoft, установщик с сайта разработчика
    Nvidia,  // ссылка на NVIDIA App со страницы nvidia.com, подпись NVIDIA
    Amd,     // ссылка на AMD Software Adrenalin со страницы amd.com, подпись AMD
    GitHub,  // установщик из последнего релиза на GitHub, проверка по SHA-256
    Page     // сайт не отдаёт файл программам — открываем страницу загрузки
}

/// <summary>Программа на вкладке «Для геймеров».</summary>
public sealed class GameApp : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    /// <summary>Иконка из Assets\apps\{Icon}.png; без неё — плитка с буквами и цветом бренда.</summary>
    public string? Icon { get; init; }
    public required string Monogram { get; init; }
    public required string Color { get; init; }
    public required AppSource Source { get; init; }
    public string? WingetId { get; init; }
    /// <summary>Репозиторий GitHub и шаблон имени установщика в релизе.</summary>
    public string? Repo { get; init; }
    public string? AssetPattern { get; init; }
    public required string PageUrl { get; init; }
    /// <summary>Регулярка по DisplayName в списке установленных программ. null — не проверяем.</summary>
    public string? DetectPattern { get; init; }

    private bool _installed, _busy, _recommended;
    private string? _status;

    public bool IsInstalled { get => _installed; set { _installed = value; Changed(); Changed(nameof(ButtonText)); } }
    public bool IsBusy { get => _busy; set { _busy = value; Changed(); Changed(nameof(CanInstall)); } }
    /// <summary>Подходит этому компьютеру: утилита производителя платы, программа для своей видеокарты.</summary>
    public bool IsRecommended { get => _recommended; set { _recommended = value; Changed(); } }
    public string? Status { get => _status; set { _status = value; Changed(); Changed(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool CanInstall => !_busy;
    public bool HasIcon => Icon is not null;

    public string ButtonText => Source == AppSource.Page ? "Открыть сайт" : IsInstalled ? "Переустановить" : "Установить";
    public string SourceText => Source switch
    {
        AppSource.Winget => "winget",
        AppSource.Nvidia => "nvidia.com",
        AppSource.Amd => "amd.com",
        AppSource.GitHub => "GitHub",
        _ => "сайт производителя"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public static class GameApps
{
    public static List<GameApp> Create() =>
    [
        // Своё — первым
        new() { Name = "Hidden Path", Icon = "hiddenpath", Description = "Обход блокировок: zapret + свой VLESS-сервер + прокси для Telegram", Category = "От Napster", Monogram = "HP", Color = "#FFFF7A00",
                Source = AppSource.GitHub, Repo = "NapsterFO/HiddenPath", AssetPattern = @"Setup.*\.exe$",
                PageUrl = "https://github.com/NapsterFO/HiddenPath", DetectPattern = @"^Hidden ?Path" },

        // Лаунчеры
        new() { Name = "Steam", Icon = "steam", Description = "Магазин и библиотека игр Valve", Category = "Лаунчеры", Monogram = "St", Color = "#FF1B2838",
                Source = AppSource.Winget, WingetId = "Valve.Steam", PageUrl = "https://store.steampowered.com/about/", DetectPattern = @"^Steam$" },
        new() { Name = "Epic Games", Icon = "epic", Description = "Лаунчер Epic Games Store и Unreal", Category = "Лаунчеры", Monogram = "Ep", Color = "#FF2A2A2A",
                Source = AppSource.Winget, WingetId = "EpicGames.EpicGamesLauncher", PageUrl = "https://store.epicgames.com/download", DetectPattern = @"^Epic Games Launcher" },
        new() { Name = "EA app", Icon = "ea", Description = "Игры Electronic Arts", Category = "Лаунчеры", Monogram = "EA", Color = "#FFFF4747",
                Source = AppSource.Winget, WingetId = "ElectronicArts.EADesktop", PageUrl = "https://www.ea.com/ea-app", DetectPattern = @"^EA app" },
        new() { Name = "Ubisoft Connect", Icon = "ubisoft", Description = "Игры Ubisoft", Category = "Лаунчеры", Monogram = "Ub", Color = "#FF0070FF",
                Source = AppSource.Winget, WingetId = "Ubisoft.Connect", PageUrl = "https://ubisoftconnect.com/", DetectPattern = @"^Ubisoft Connect" },
        new() { Name = "Battle.net", Icon = "battlenet", Description = "Игры Blizzard и Activision", Category = "Лаунчеры", Monogram = "Bn", Color = "#FF148EFF",
                Source = AppSource.Winget, WingetId = "Blizzard.BattleNet", PageUrl = "https://download.battle.net/", DetectPattern = @"^Battle\.net" },

        // Общение
        new() { Name = "Discord", Icon = "discord", Description = "Голосовые и текстовые чаты", Category = "Общение", Monogram = "Dc", Color = "#FF5865F2",
                Source = AppSource.Winget, WingetId = "Discord.Discord", PageUrl = "https://discord.com/download", DetectPattern = @"^Discord$" },
        new() { Name = "TeamSpeak 3", Icon = "teamspeak", Description = "Голосовая связь с низкой задержкой", Category = "Общение", Monogram = "TS", Color = "#FF2580C3",
                Source = AppSource.Winget, WingetId = "TeamSpeakSystems.TeamSpeakClient", PageUrl = "https://www.teamspeak.com/downloads/", DetectPattern = @"^TeamSpeak" },

        // Видеокарта
        new() { Name = "NVIDIA App", Icon = "nvidia", Description = "Драйверы GeForce, настройки игр, запись", Category = "Видеокарта", Monogram = "Nv", Color = "#FF76B900",
                Source = AppSource.Nvidia, PageUrl = "https://www.nvidia.com/en-us/software/nvidia-app/", DetectPattern = @"^NVIDIA app" },
        new() { Name = "AMD Software: Adrenalin", Icon = "amd", Description = "Драйверы Radeon и игровые настройки", Category = "Видеокарта", Monogram = "AMD", Color = "#FFED1C24",
                Source = AppSource.Amd, PageUrl = "https://www.amd.com/en/support/download/drivers.html", DetectPattern = @"^AMD Software" },

        // Утилиты производителей
        new() { Name = "MSI Center", Icon = "msi", Description = "Подсветка, вентиляторы и режимы MSI", Category = "Утилиты производителей", Monogram = "MSI", Color = "#FFE3000F",
                Source = AppSource.Winget, WingetId = "MSI.MSICenter", PageUrl = "https://www.msi.com/Landing/MSI-Center", DetectPattern = @"^MSI Center" },
        new() { Name = "ASUS Armoury Crate", Icon = "armoury", Description = "Подсветка Aura, вентиляторы и обновления ASUS", Category = "Утилиты производителей", Monogram = "AC", Color = "#FF00539B",
                Source = AppSource.Winget, WingetId = "Asus.ArmouryCrate", PageUrl = "https://rog.asus.com/armoury-crate/", DetectPattern = @"Armoury ?Crate" },
        new() { Name = "GIGABYTE Control Center", Icon = "gigabyte", Description = "Подсветка, вентиляторы и обновления GIGABYTE", Category = "Утилиты производителей", Monogram = "GB", Color = "#FFF15A24",
                Source = AppSource.Page, PageUrl = "https://www.gigabyte.com/Support/Utility", DetectPattern = @"^GIGABYTE Control Center" },
        new() { Name = "MSI Afterburner", Icon = "afterburner", Description = "Разгон и мониторинг видеокарты", Category = "Утилиты производителей", Monogram = "AB", Color = "#FFB3001B",
                Source = AppSource.Winget, WingetId = "Guru3D.Afterburner", PageUrl = "https://www.msi.com/Landing/afterburner", DetectPattern = @"^MSI Afterburner" },

        // Периферия
        new() { Name = "Logitech G HUB", Icon = "ghub", Description = "Мыши, клавиатуры и гарнитуры Logitech G", Category = "Периферия", Monogram = "G", Color = "#FF00B8FC",
                Source = AppSource.Winget, WingetId = "Logitech.GHUB", PageUrl = "https://www.logitechg.com/innovation/g-hub.html", DetectPattern = @"^Logitech G HUB" },
        new() { Name = "Razer Synapse 4", Icon = "razer", Description = "Устройства Razer и подсветка Chroma", Category = "Периферия", Monogram = "Rz", Color = "#FF44D62C",
                Source = AppSource.Winget, WingetId = "RazerInc.RazerInstaller.Synapse4", PageUrl = "https://www.razer.com/synapse-4", DetectPattern = @"^Razer Synapse" },
        new() { Name = "SteelSeries GG", Icon = "steelseries", Description = "Устройства SteelSeries и Moments", Category = "Периферия", Monogram = "SS", Color = "#FFFF5200",
                Source = AppSource.Winget, WingetId = "SteelSeries.GG", PageUrl = "https://steelseries.com/gg", DetectPattern = @"^SteelSeries GG" },
        new() { Name = "Corsair iCUE", Icon = "corsair", Description = "Устройства и подсветка Corsair", Category = "Периферия", Monogram = "iC", Color = "#FFECE81A",
                Source = AppSource.Winget, WingetId = "Corsair.iCUE.5", PageUrl = "https://www.corsair.com/icue", DetectPattern = @"iCUE" },

        // Мониторинг и запись
        new() { Name = "HWiNFO", Icon = "hwinfo", Description = "Подробные датчики и сведения о железе", Category = "Мониторинг и запись", Monogram = "HW", Color = "#FF3C6FB6",
                Source = AppSource.Winget, WingetId = "REALiX.HWiNFO", PageUrl = "https://www.hwinfo.com/download/", DetectPattern = @"^HWiNFO" },
        new() { Name = "CPU-Z", Icon = "cpuz", Description = "Процессор, память и материнская плата", Category = "Мониторинг и запись", Monogram = "CZ", Color = "#FF4B5FA6",
                Source = AppSource.Winget, WingetId = "CPUID.CPU-Z", PageUrl = "https://www.cpuid.com/softwares/cpu-z.html", DetectPattern = @"CPU-Z" },
        new() { Name = "GPU-Z", Icon = "gpuz", Description = "Сведения о видеокарте и датчики", Category = "Мониторинг и запись", Monogram = "GZ", Color = "#FF8E1C1C",
                Source = AppSource.Winget, WingetId = "TechPowerUp.GPU-Z", PageUrl = "https://www.techpowerup.com/gpuz/", DetectPattern = @"GPU-Z" },
        new() { Name = "OBS Studio", Icon = "obs", Description = "Запись и стримы", Category = "Мониторинг и запись", Monogram = "OBS", Color = "#FF302E31",
                Source = AppSource.Winget, WingetId = "OBSProject.OBSStudio", PageUrl = "https://obsproject.com/download", DetectPattern = @"^OBS Studio" },

        // Компоненты, без которых не запускаются игры
        new() { Name = "Visual C++ Redistributable", Icon = "vcpp", Description = "Библиотеки Microsoft, нужные большинству игр", Category = "Компоненты Windows", Monogram = "C++", Color = "#FF68217A",
                Source = AppSource.Winget, WingetId = "Microsoft.VCRedist.2015+.x64", PageUrl = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
                DetectPattern = @"Visual C\+\+ (2015-20\d\d|v14) .*x64" },
        new() { Name = "DirectX (среда выполнения)", Icon = "directx", Description = "Старые компоненты DirectX 9–11 для игр", Category = "Компоненты Windows", Monogram = "DX", Color = "#FF107C10",
                Source = AppSource.Winget, WingetId = "Microsoft.DirectX", PageUrl = "https://www.microsoft.com/download/details.aspx?id=35" },
    ];

    /// <summary>Отмечает установленные и подходящие этому компьютеру программы.</summary>
    public static void Refresh(IEnumerable<GameApp> apps, SystemProfile? system)
    {
        var installed = InstalledNames();
        var gpus = VideoControllers();
        var maker = system is null ? "" : $"{system.Manufacturer} {system.BoardManufacturer}";
        foreach (var app in apps)
        {
            app.IsInstalled = app.DetectPattern is { } p && installed.Any(n => Regex.IsMatch(n, p, RegexOptions.IgnoreCase));
            app.IsRecommended = app.Name switch
            {
                "NVIDIA App" => gpus.Any(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)),
                "AMD Software: Adrenalin" => gpus.Any(g => g.Contains("Radeon", StringComparison.OrdinalIgnoreCase)),
                "MSI Center" => maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || maker.Contains("MSI", StringComparison.Ordinal),
                "ASUS Armoury Crate" => maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase),
                "GIGABYTE Control Center" => maker.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
    }

    /// <summary>Устанавливает программу. Установщики с сайтов открываются в своём окне — как и у драйверов.</summary>
    public static async Task<(bool ok, string message)> InstallAsync(GameApp app, Action<string> progress)
    {
        switch (app.Source)
        {
            case AppSource.Winget:
                return await WingetAsync(app, progress).ConfigureAwait(false);

            case AppSource.Nvidia:
            {
                progress("Ищу последнюю версию на nvidia.com…");
                var html = await VendorSources.GetStringWithRetryAsync(app.PageUrl).ConfigureAwait(false);
                var url = Regex.Match(html, @"https://[a-z.]*download\.nvidia\.com/nvapp/client/[\d.]+/NVIDIA_app_v[\d.]+\.exe").Value;
                if (url.Length == 0) return OpenPage(app, "Не нашёл ссылку на nvidia.com — открыл страницу загрузки");
                return await VendorInstaller.DownloadAndRunAsync(new DriverUpdate
                {
                    Title = app.Name, Source = "nvidia.com", Kind = DriverKind.Installer, DownloadUrl = url, ExpectedSigner = "NVIDIA"
                }, progress).ConfigureAwait(false);
            }

            case AppSource.Amd:
            {
                progress("Ищу последнюю версию на amd.com…");
                var html = await VendorSources.GetStringWithRetryAsync(app.PageUrl).ConfigureAwait(false);
                var url = Regex.Match(html, @"https://drivers\.amd\.com/drivers/installer/[^""'\s]+minimalsetup[^""'\s]*\.exe").Value;
                if (url.Length == 0) return OpenPage(app, "Не нашёл ссылку на amd.com — открыл страницу загрузки");
                return await VendorInstaller.DownloadAndRunAsync(new DriverUpdate
                {
                    Title = app.Name, Source = "amd.com", Kind = DriverKind.Installer, DownloadUrl = url,
                    Referer = "https://www.amd.com/", ExpectedSigner = "Advanced Micro Devices"
                }, progress).ConfigureAwait(false);
            }

            case AppSource.GitHub:
            {
                progress("Ищу последний релиз на GitHub…");
                var release = await GitHubReleases.LatestAsync(app.Repo!, app.AssetPattern!).ConfigureAwait(false);
                if (release is null || GitHubReleases.ToDownload(release, app.Name) is not { } download)
                    return OpenPage(app, "В последнем релизе нет установщика с контрольной суммой — открыл страницу");
                return await VendorInstaller.DownloadAndRunAsync(download, progress).ConfigureAwait(false);
            }

            default:
                return OpenPage(app, "Открыл страницу загрузки на сайте производителя");
        }
    }

    private static async Task<(bool ok, string message)> WingetAsync(GameApp app, Action<string> progress)
    {
        var winget = FindWinget();
        if (winget is null) return OpenPage(app, "На компьютере нет winget (App Installer) — открыл страницу загрузки");

        progress("Скачиваю и устанавливаю через winget…");
        var psi = new ProcessStartInfo(winget)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in new[] { "install", "--id", app.WingetId!, "--exact", "--source", "winget", "--silent",
                                  "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return (false, "Не удалось запустить winget");
        var output = proc.StandardOutput.ReadToEndAsync();
        await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);

        // Коды winget: 0 — готово; 0x8A15002B — уже стоит последняя версия; 0x8A150101 — установщик просит перезагрузку.
        return unchecked((uint)proc.ExitCode) switch
        {
            0 => (true, "Установлено"),
            0x8A15002B => (true, "Уже установлена последняя версия"),
            0x8A150101 or 0x8A150109 => (true, "Установлено, нужна перезагрузка"),
            0x8A150014 => (false, "winget не нашёл пакет — попробуйте с сайта"),
            0x8A150011 or 0x8A15000F => (false, "Нет доступа к источнику winget — проверьте интернет"),
            _ => (false, $"winget завершился с кодом 0x{proc.ExitCode:X8}: " + LastLine(await output.ConfigureAwait(false)))
        };
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Any(char.IsLetter)) ?? "";

    /// <summary>winget.exe — псевдоним из App Installer в WindowsApps текущего пользователя.</summary>
    private static string? FindWinget()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "winget.exe")).FirstOrDefault(File.Exists);
    }

    private static (bool ok, string message) OpenPage(GameApp app, string message)
    {
        Process.Start(new ProcessStartInfo(app.PageUrl) { UseShellExecute = true });
        return (app.Source == AppSource.Page, message);
    }

    /// <summary>DisplayName всех установленных программ: машинные (64 и 32 бита) и текущего пользователя.</summary>
    private static List<string> InstalledNames()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var names = new List<string>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                                             (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(key);
                if (root is null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string n && n.Length > 0) names.Add(n.Trim());
                }
            }
            catch { /* нет доступа к ветке — пропускаем */ }
        }
        return names;
    }

    private static List<string> VideoControllers()
    {
        var list = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    if (mo["Name"] is string n) list.Add(n);
        }
        catch { /* WMI недоступен — без рекомендаций */ }
        return list;
    }
}
