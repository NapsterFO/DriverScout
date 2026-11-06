using System.Net.Http;
using System.Text;

namespace DriverScout;

/// <summary>Сводит устройства и найденные драйверы в строки таблицы.</summary>
public sealed class ScanService
{
    public WindowsUpdateSource? WindowsUpdate { get; private set; }
    private readonly HashSet<string> _errors = [];
    public string? SourceError => _errors.Count == 0 ? null : string.Join("; ", _errors);
    /// <summary>Производитель компьютера, если удалось определить.</summary>
    public OemInfo? Oem { get; private set; }

    public List<DriverRow> Run(Action<string> progress)
    {
        progress("Сканирую устройства…");
        var devices = DeviceScanner.Scan();
        var system = SystemProfile.Load();
        Oem = OemBrands.Detect(system);

        // Windows Update ищет дольше всех (до пары минут) и ждёт сервер, а не нас — пусть идёт параллельно
        // с сайтами производителей и каталогом.
        progress("Ищу драйверы в Windows Update и у производителей…");
        var wuTask = Task.Run(() =>
        {
            try
            {
                WindowsUpdate = new WindowsUpdateSource();
                return WindowsUpdate.Search();
            }
            catch (Exception ex)
            {
                lock (_errors) _errors.Add("Windows Update: " + ex.Message);
                return [];
            }
        });

        TechPowerUp.Reset();
        var sources = VendorSources.Create();
        var vendor = new Dictionary<DeviceInfo, VendorResult>();
        foreach (var dev in devices)
        {
            // Идём по источникам в порядке приоритета: первый нашедший обновление побеждает,
            // ссылку на сайт берём от первого, кто её дал.
            VendorResult? chosen = null;
            string? page = null;
            foreach (var source in sources.Where(s => s.Handles(dev, system)))
            {
                progress($"Проверяю {source.Name}: {dev.Name}…");
                try
                {
                    var r = source.CheckAsync(dev, system).GetAwaiter().GetResult();
                    page ??= r.PageUrl;
                    if (r.Update is not null) { chosen = r; break; }
                }
                catch (Exception ex)
                {
                    lock (_errors) _errors.Add($"{source.Name}: {ex.Message}");
                }
            }
            if (chosen is not null || page is not null)
                vendor[dev] = chosen ?? new VendorResult(null, page);
        }

        // Каталог Microsoft. В нём лежат драйверы, которые Windows Update этому компьютеру сам не предлагает,
        // и почти всё от Intel, Realtek, MediaTek, Qualcomm (сайты которых программам не отвечают).
        // Сначала — устройства без драйвера или с ошибкой, потом — свежие версии для работающих.
        UpdateCatalog.BeginScan();
        var extra = new Dictionary<DeviceInfo, DriverUpdate>();
        var broken = devices.Where(d => (d.IsMissingDriver || d.HasProblem) && d.HardwareIds.Length > 0
                                        && vendor.GetValueOrDefault(d)?.Update is null).ToList();
        var working = devices.Where(d => !broken.Contains(d) && vendor.GetValueOrDefault(d)?.Update is null && WorthCatalogCheck(d))
            .OrderByDescending(d => VendorSources.IsVendor(d, "8086") || d.HardwareIds.Any(h => h.Contains("VID_8087", StringComparison.OrdinalIgnoreCase)))
            .Take(MaxCatalogChecks).ToList();
        int n = 0, done = 0, total = broken.Count + working.Count;
        foreach (var dev in broken.Concat(working))
        {
            bool isBroken = broken.Contains(dev);
            try
            {
                if (UpdateCatalog.Throttled) throw new HttpRequestException("каталог временно ограничил поиск");
                progress($"Ищу в каталоге Microsoft ({++n} из {total}): {dev.Name}…");
                var found = isBroken
                    ? UpdateCatalog.FindAsync(dev, system.OsBuild).GetAwaiter().GetResult()
                    : UpdateCatalog.FindAsync(dev, system.OsBuild, 2, VendorSources.ParseVersion(dev.DriverVersion)).GetAwaiter().GetResult();
                if (found is not null) extra[dev] = found;
                done++;
            }
            catch (Exception ex)
            {
                // Для работающих устройств это не беда: проверенное уже в кэше, остальное досмотрим при следующем скане.
                lock (_errors) _errors.Add(isBroken || ex is not HttpRequestException
                    ? $"{UpdateCatalog.SourceName}: {ex.Message}"
                    : $"{UpdateCatalog.SourceName}: проверено {done} из {total} устройств — каталог ограничил число запросов, " +
                      "остальные проверятся при следующем сканировании");
                break; // нет интернета или каталог ограничил поиск — не ждём таймаута на каждом устройстве
            }
        }

        if (!wuTask.IsCompleted) progress("Жду ответа Windows Update…");
        var updates = wuTask.GetAwaiter().GetResult();

        // Драйверпаки SDIO — последним: только для устройств, которым ничего не нашлось в официальных источниках.
        // Сетевые паки встроены в программу, поэтому этот шаг работает и без интернета.
        var sdi = extra;
        if (Sdi.SdiService.HasIndexes)
        {
            try
            {
                var rest = devices.Where(d => vendor.GetValueOrDefault(d)?.Update is null && FindUpdate(d, updates) is null
                                              && !extra.ContainsKey(d)).ToList();
                foreach (var (dev, upd) in Sdi.SdiService.Match(rest, system, progress)) sdi[dev] = upd;
            }
            catch (Exception ex)
            {
                _errors.Add("SDIO: " + ex.Message);
            }
        }

        return Match(devices, updates, vendor, sdi);
    }

    public static List<DriverRow> Match(List<DeviceInfo> devices, List<DriverUpdate> updates,
                                        Dictionary<DeviceInfo, VendorResult> vendor,
                                        Dictionary<DeviceInfo, DriverUpdate>? sdi = null)
    {
        var rows = new List<DriverRow>();
        var used = new HashSet<DriverUpdate>();

        foreach (var dev in devices)
        {
            var wu = FindUpdate(dev, updates);
            if (wu is not null) used.Add(wu);
            var vr = vendor.GetValueOrDefault(dev);
            // Драйвер с сайта производителя проверен как более новый — он важнее Windows Update.
            // Из каталога Microsoft берём, если там версия новее той, что предлагает Windows Update.
            var other = sdi?.GetValueOrDefault(dev);
            var upd = vr?.Update
                      ?? (wu is not null && other?.Kind == DriverKind.Catalog
                          && VendorSources.ParseVersion(other.Version) > VendorSources.ParseVersion(wu.Version) ? other : wu)
                      ?? other;

            var status = dev.IsMissingDriver ? RowStatus.Missing
                       : upd is not null ? RowStatus.Update
                       : dev.HasProblem ? RowStatus.Problem
                       : RowStatus.UpToDate;

            var row = new DriverRow
            {
                Status = status,
                Name = dev.Name,
                DeviceClass = dev.DeviceClass,
                HardwareId = dev.HardwareIds.FirstOrDefault() ?? dev.DeviceId,
                CurrentVersion = dev.DriverVersion,
                CurrentDate = dev.DriverDate,
                Update = upd,
                VendorPage = vr?.PageUrl,
                Result = upd?.Note
                         ?? (status == RowStatus.Missing ? "Драйвер не найден ни в Windows Update, ни у производителей, ни в каталоге Microsoft" : "")
            };
            row.IsSelected = upd is not null && !row.IsOlderThanInstalled && (upd.Note is null || upd.AutoSelect);
            rows.Add(row);
        }

        // Драйверы, которые Windows Update предложил, но устройства среди подключённых нет:
        // чаще всего это когда-то подключавшееся и сейчас отсутствующее железо
        // (WMI такие устройства не показывает). Показываем, но к установке не отмечаем.
        foreach (var upd in updates.Where(u => !used.Contains(u)))
        {
            rows.Add(new DriverRow
            {
                Status = RowStatus.Update,
                Name = (upd.Model ?? upd.Title) + " (устройство не найдено — возможно, отключено)",
                HardwareId = upd.HardwareId,
                Update = upd
            });
        }

        return rows.OrderBy(r => r.Status).ThenBy(r => r.Name).ToList();
    }

    /// <summary>Сколько работающих устройств проверять в каталоге за скан: по секунде на запрос, больше — уже долго.</summary>
    private const int MaxCatalogChecks = 40;

    /// <summary>
    /// Работающее устройство стоит проверить в каталоге: драйвер производителя (не стандартный Microsoft — тот
    /// нумеруется по-своему, а заменять его без нужды незачем) на «настоящей» шине. Видеокарты NVIDIA и AMD
    /// проверяются на их сайтах, программные и виртуальные устройства (SWD, ROOT, тома и т. п.) в каталоге не ищем.
    /// </summary>
    private static bool WorthCatalogCheck(DeviceInfo d)
    {
        if (d.HardwareIds.Length == 0 || VendorSources.IsInboxDriver(d) || VendorSources.ParseVersion(d.DriverVersion) is null)
            return false;
        if ((VendorSources.IsVendor(d, "10DE") || VendorSources.IsVendor(d, "1002"))
            && string.Equals(d.DeviceClass, "Display", StringComparison.OrdinalIgnoreCase)) return false;
        var bus = d.HardwareIds[0].Split('\\')[0].ToUpperInvariant();
        return bus is "PCI" or "USB" or "ACPI" or "HDAUDIO" or "INTELAUDIO" or "BTH";
    }

    // Hardware ID перечислены от самого точного к самому общему — берём первое совпадение.
    private static DriverUpdate? FindUpdate(DeviceInfo dev, List<DriverUpdate> updates)
    {
        foreach (var hwid in dev.HardwareIds)
        {
            var upd = updates.FirstOrDefault(u => string.Equals(u.HardwareId, hwid, StringComparison.OrdinalIgnoreCase));
            if (upd is not null) return upd;
        }
        return null;
    }

    /// <summary>Строка о производителе компьютера для окна и отчёта.</summary>
    public static string? DescribeOem(OemInfo? oem) => oem is null ? null :
        $"Компьютер: {oem.Brand} {oem.Model}. " +
        (oem.AutoCheck ? "Драйверы производителя проверяются автоматически." : "Автоматической проверки для этого бренда нет.") +
        (oem.Utility is null ? "" : $" Фирменная утилита: {oem.Utility}.") +
        (oem.Remark is null ? "" : $" {oem.Remark}.");

    public static string BuildReport(List<DriverRow> rows, string? sourceError, OemInfo? oem = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DriverScout — отчёт {DateTime.Now:dd.MM.yyyy HH:mm}");
        if (oem is not null) sb.AppendLine(DescribeOem(oem)).AppendLine("Страница драйверов: " + oem.SupportUrl);
        if (sourceError is not null) sb.AppendLine("ВНИМАНИЕ: " + sourceError);
        foreach (var group in rows.GroupBy(r => r.Status))
        {
            sb.AppendLine();
            sb.AppendLine($"== {group.First().StatusText} ({group.Count()}) ==");
            foreach (var r in group)
            {
                // Среди актуальных показываем только те, что проверены на сайте производителя.
                if (group.Key == RowStatus.UpToDate && r.VendorPage is null) continue;
                sb.AppendLine($"- {r.Name} [{r.HardwareId}]");
                sb.AppendLine($"    сейчас: {r.Current}");
                if (r.Update is not null)
                    sb.AppendLine($"    найдено: {r.Available} — {r.Update.Title} ({r.Source})");
                if (r.Update?.DownloadUrl is not null) sb.AppendLine($"    файл: {r.Update.DownloadUrl}");
                if (!string.IsNullOrEmpty(r.Update?.Note)) sb.AppendLine($"    внимание: {r.Update.Note}");
                if (r.VendorPage is not null) sb.AppendLine($"    сайт производителя: {r.VendorPage}");
            }
        }
        return sb.ToString();
    }
}
