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

        var updates = new List<DriverUpdate>();
        progress("Ищу драйверы в Windows Update (может занять пару минут)…");
        try
        {
            WindowsUpdate = new WindowsUpdateSource();
            updates = WindowsUpdate.Search();
        }
        catch (Exception ex)
        {
            _errors.Add("Windows Update: " + ex.Message);
        }

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
                    _errors.Add($"{source.Name}: {ex.Message}");
                }
            }
            if (chosen is not null || page is not null)
                vendor[dev] = chosen ?? new VendorResult(null, page);
        }

        // Драйверпаки SDIO — последним: только для устройств, которым ничего не нашлось в официальных источниках.
        // Сетевые паки встроены в программу, поэтому этот шаг работает и без интернета.
        var sdi = new Dictionary<DeviceInfo, DriverUpdate>();
        if (Sdi.SdiService.HasIndexes)
        {
            try
            {
                var rest = devices.Where(d => vendor.GetValueOrDefault(d)?.Update is null && FindUpdate(d, updates) is null).ToList();
                sdi = Sdi.SdiService.Match(rest, system, progress);
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
            var upd = vr?.Update ?? wu ?? sdi?.GetValueOrDefault(dev);

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
                         ?? (status == RowStatus.Missing ? "Драйвер не найден ни в Windows Update, ни у производителей" : "")
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
