// DriverScout — модуль драйверпаков Snappy Driver Installer Origin.
// Переработка кода SDIO (install.cpp, © Glenn Delahoy и авторы SDI/SDIO), распространяется по GNU GPL v3.

using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace DriverScout.Sdi;

/// <summary>Драйверпаки SDIO в DriverScout: индексы, подбор при сканировании, загрузка паков и установка.</summary>
public static class SdiService
{
    public const string SourceName = "SDIO";

    /// <summary>Папка с индексами внутри скачанного торрента (…\SDIO_Update\indexes\SDIO) или null.</summary>
    public static string? IndexDir =>
        Directory.Exists(SdiTorrent.Root)
            ? Directory.EnumerateDirectories(SdiTorrent.Root).Select(d => Path.Combine(d, "indexes", "SDIO"))
                .FirstOrDefault(d => Directory.Exists(d) && Directory.EnumerateFiles(d, "*.bin").Any())
            : null;

    /// <summary>
    /// Встроенные в установщик сетевые драйверпаки ({app}\Drivers\indexes и \drivers): без драйвера сетевой карты
    /// интернета нет, а значит, нет ни Windows Update, ни торрента.
    /// </summary>
    public static string BundledDir { get; } = Path.Combine(AppContext.BaseDirectory, "Drivers");

    /// <summary>Какие драйверпаки встраивать (имена без номера выпуска).</summary>
    public static readonly string[] BundledFamilies = ["DP_LAN_Intel", "DP_LAN_Realtek-NT", "DP_LAN_Others"];

    /// <summary>Есть индексы — скачанные или встроенные.</summary>
    public static bool HasIndexes => IndexFiles().Count > 0;

    /// <summary>
    /// Индексы для подбора. Один пак может лежать и среди встроенных, и среди скачанных: берём тот, чей архив
    /// уже на диске (чтобы ставить без интернета), а из них — самый свежий выпуск.
    /// </summary>
    private static List<string> IndexFiles()
    {
        var bundled = Path.Combine(BundledDir, "indexes");
        var files = (IndexDir is { } dir ? Directory.GetFiles(dir, "*.bin") : [])
            .Concat(Directory.Exists(bundled) ? Directory.GetFiles(bundled, "*.bin") : []);
        return files.GroupBy(f => PackFamily(Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => PackFile(Path.GetFileNameWithoutExtension(f)) is not null)
                          .ThenByDescending(f => PackRelease(Path.GetFileNameWithoutExtension(f))).First())
            .ToList();
    }

    /// <summary>"DP_LAN_Intel_26081" → "DP_LAN_Intel".</summary>
    public static string PackFamily(string pack) => Regex.Replace(pack, @"_\d+$", "");
    private static int PackRelease(string pack) =>
        Regex.Match(pack, @"_(\d+)$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    /// <summary>Архив драйверпака на диске: сначала встроенный, потом скачанный торрентом.</summary>
    public static string? PackFile(string packName) =>
        new[] { Path.Combine(BundledDir, "drivers") }
            .Concat(Directory.Exists(SdiTorrent.Root)
                ? Directory.EnumerateDirectories(SdiTorrent.Root).Select(d => Path.Combine(d, "drivers"))
                : [])
            .Select(d => Path.Combine(d, packName + ".7z")).FirstOrDefault(File.Exists);

    private static bool IsBundled(string path) =>
        path.StartsWith(BundledDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Для сборки установщика: скачивает встроенные сетевые драйверпаки с индексами и раскладывает в target
    /// (target\indexes\*.bin, target\drivers\*.7z). Старые выпуски из target удаляются.
    /// </summary>
    public static async Task FetchBundledAsync(string target, Action<string> progress, CancellationToken ct)
    {
        progress("Подключаюсь к раздаче SDIO…");
        await using var torrent = new SdiTorrent();
        await torrent.OpenAsync();
        bool Wanted(string p, string folder, string ext) =>
            p.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && p.EndsWith(ext, StringComparison.OrdinalIgnoreCase) &&
            BundledFamilies.Contains(PackFamily(Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
        var files = torrent.Files.Where(p => Wanted(p, "indexes/SDIO/", ".bin") || Wanted(p, "drivers/", ".7z")).ToList();
        if (files.Count != BundledFamilies.Length * 2)
            throw new InvalidOperationException("В раздаче не нашлись все сетевые драйверпаки: " + string.Join(", ", files));
        await torrent.DownloadAsync(files, (d, t) => progress(Percent("Скачиваю сетевые драйверпаки", d, t)), ct);

        foreach (var sub in new[] { "indexes", "drivers" })
        {
            var dir = Directory.CreateDirectory(Path.Combine(target, sub)).FullName;
            foreach (var old in Directory.GetFiles(dir)) File.Delete(old);
        }
        foreach (var p in files)
            File.Copy(torrent.LocalPath(p), Path.Combine(target, p.StartsWith("drivers/") ? "drivers" : "indexes", Path.GetFileName(p)));
    }

    /// <summary>Скачивает или обновляет индексы всех драйверпаков (~25 МБ), старые удаляет.</summary>
    public static async Task DownloadIndexesAsync(Action<string> progress, CancellationToken ct)
    {
        progress("Подключаюсь к раздаче SDIO…");
        await using var torrent = new SdiTorrent();
        await torrent.OpenAsync();
        var indexes = torrent.Files.Where(IsIndex).ToList();
        await torrent.DownloadAsync(indexes, (d, t) => progress(Percent("Скачиваю индексы драйверпаков", d, t)), ct);

        // Индексы прошлых выпусков ссылаются на паки, которых в раздаче уже нет.
        var current = indexes.Select(torrent.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dir = Path.GetDirectoryName(current.First())!;
        foreach (var old in Directory.EnumerateFiles(dir, "*.bin").Where(f => !current.Contains(f)).ToList())
            File.Delete(old);
    }

    private static bool IsIndex(string p) => p.StartsWith("indexes/SDIO/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".bin");

    /// <summary>
    /// Подбор драйверов для устройств, которым не нашлось обновления у производителя и в Windows Update.
    /// Возвращает предложения по устройствам.
    /// </summary>
    public static Dictionary<DeviceInfo, DriverUpdate> Match(List<DeviceInfo> devices, SystemProfile sys, Action<string> progress)
    {
        var result = new Dictionary<DeviceInfo, DriverUpdate>();
        var indexes = IndexFiles();
        if (indexes.Count == 0) return result;

        progress("Читаю индексы драйверпаков SDIO…");
        var wanted = devices.SelectMany(d => d.HardwareIds.Concat(d.CompatibleIds))
            .Select(s => s.ToUpperInvariant()).ToHashSet();
        var packs = indexes.AsParallel()
            .Select(f => { try { return SdiIndex.Load(f, wanted); } catch (InvalidDataException) { return null; } })
            .OfType<SdiIndex>().Where(p => p.Hwids.Count > 0).ToList();

        progress("Подбираю драйверы из драйверпаков SDIO…");
        var matcher = new SdiMatcher(sys);
        foreach (var d in devices.Where(d => d.HardwareIds.Length > 0))
        {
            var installed = InstalledDriver.Read(d);
            var candidates = matcher.Match(d, installed, packs);
            var best = SdiMatcher.Pick(candidates, d, installed);
            // Без драйвера устройство может оказаться сетевой картой, а без неё нет интернета, чтобы скачать пак, —
            // поэтому драйвер из пака на диске (встроенного) важнее чуть более свежего из пака, которого нет.
            if (d.IsMissingDriver && best is not null && PackFile(best.Pack.PackName) is null)
                best = SdiMatcher.Pick(candidates.Where(c => PackFile(c.Pack.PackName) is not null).ToList(), d, installed) ?? best;
            if (best is null) continue;
            bool bundled = PackFile(best.Pack.PackName) is { } file && IsBundled(file);
            result[d] = new DriverUpdate
            {
                Title = $"SDIO: {best.Description} {best.Version}",
                Source = SourceName,
                Kind = DriverKind.DriverPack,
                Version = best.Version.ToString(),
                Date = best.Version.Date,
                HardwareId = best.HardwareId,
                Manufacturer = best.Provider,
                PackName = best.Pack.PackName,
                PackInf = best.Inf.Path + best.Inf.Name,
                Bundled = bundled,
                // Сетевой карте без драйвера ставим встроенный сразу: иначе не будет интернета и для остального.
                AutoSelect = bundled && d.IsMissingDriver,
                Note = bundled
                    ? $"Встроенный сетевой драйвер из драйверпака SDIO ({best.Pack.PackName}) — ставится без интернета; сторонняя сборка, не с сайта производителя"
                    : $"Драйвер из драйверпака SDIO ({best.Pack.PackName}) — сторонняя сборка, не с сайта производителя"
            };
        }
        return result;
    }

    /// <summary>Скачивает драйверпаки для выбранных драйверов одной раздачей (поиск пиров занимает время).</summary>
    public static async Task DownloadPacksAsync(IEnumerable<DriverUpdate> updates, Action<string> progress, CancellationToken ct)
    {
        var packs = updates.Where(u => PackFile(u.PackName!) is null)
            .Select(u => $"drivers/{u.PackName}.7z").Distinct().ToList();
        if (packs.Count == 0) return; // всё уже на диске — встроено или скачано раньше
        progress("Подключаюсь к раздаче SDIO…");
        await using var torrent = new SdiTorrent();
        await torrent.OpenAsync();
        var missing = packs.Where(p => !torrent.Files.Contains(p)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("Драйверпака нет в текущей раздаче — обновите индексы SDIO: " + string.Join(", ", missing));
        await torrent.DownloadAsync(packs, (d, t) => progress(Percent("Скачиваю драйверпаки", d, t)), ct);
    }

    /// <summary>Распаковывает папку INF из драйверпака и ставит драйвер для устройства (как SDIO, с INSTALLFLAG_FORCE).</summary>
    public static (bool ok, bool reboot, string message) Install(DriverUpdate update, Action<string> progress)
    {
        var pack = PackFile(update.PackName!);
        if (pack is null) return (false, false, "Драйверпак не скачан");

        var infDir = Path.GetDirectoryName(update.PackInf!)!.Replace('/', '\\').TrimEnd('\\') + "\\";
        var target = Path.Combine(SdiTorrent.Root, "extract", update.PackName!);
        progress("Распаковываю драйвер из драйверпака…");
        int files = Extract(pack, infDir, target);
        if (files == 0) return (false, false, "В драйверпаке нет папки " + infDir);

        var inf = Path.Combine(target, update.PackInf!.Replace('/', '\\'));
        progress("Устанавливаю драйвер…");
        return InstallInf(update.HardwareId!, inf);
    }

    /// <summary>Ставит INF устройствам с этим Hardware ID (как SDIO, с INSTALLFLAG_FORCE). Подпись проверяет Windows.</summary>
    public static (bool ok, bool reboot, string message) InstallInf(string hardwareId, string inf)
    {
        const uint INSTALLFLAG_FORCE = 1;
        if (UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, inf, INSTALLFLAG_FORCE, out var reboot))
            return (true, reboot, reboot ? "Драйвер установлен, нужна перезагрузка" : "Драйвер установлен");
        int err = Marshal.GetLastWin32Error();
        return (false, false, $"Windows не установила драйвер: {new System.ComponentModel.Win32Exception(err).Message} (0x{err:X8})");
    }

    /// <summary>Достаёт из 7z только файлы папки драйвера. Архивы SDIO сплошные — читаем потоком.</summary>
    private static int Extract(string archivePath, string folder, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        int count = 0;
        using var archive = SevenZipArchive.OpenArchive(archivePath, new ReaderOptions());
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            var key = (entry.Key ?? "").Replace('/', '\\');
            if (entry.IsDirectory || !key.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.GetFullPath(Path.Combine(root, key));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // защита от «..\» в именах
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            reader.WriteEntryTo(file);
            count++;
        }
        return count;
    }

    private static string Percent(string what, long done, long total) =>
        total > 0 ? $"{what}: {done * 100 / total}% из {Math.Max(1, total / 1048576)} МБ" : what + "…";

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath,
                                                                  uint installFlags, out bool rebootRequired);
}
