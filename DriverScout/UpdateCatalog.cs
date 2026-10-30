using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace DriverScout;

/// <summary>
/// Каталог Центра обновления Майкрософт (catalog.update.microsoft.com) — все драйверы, которые производители
/// выложили через Windows Update, в том числе те, что Windows Update этому компьютеру сам не предлагает
/// (Intel GPIO, HID Event Filter, Management Engine, Motorcomm и т. п.). Поиск — по Hardware ID,
/// файлы — .cab, подписанные Microsoft, со SHA-1 из каталога.
/// </summary>
public static class UpdateCatalog
{
    public const string SourceName = "Каталог Microsoft";
    private const string Base = "https://www.catalog.update.microsoft.com/";
    /// <summary>Сколько вариантов пробовать при установке: поиск по общему ID находит и чужие сборки (другая плата, ARM64).</summary>
    private const int MaxCandidates = 5;
    /// <summary>Пауза между поисковыми запросами: подряд без пауз каталог начинает отвечать «searchThrottled».</summary>
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(1);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastSearch;
    private static readonly Regex RowRegex = new(@"<tr id=""([0-9a-f\-]{36})_R\d+""(.*?)</tr>", RegexOptions.Singleline);

    public sealed record Entry(string UpdateId, string Title, string Products, DateTime? Date, Version? Version, long Size)
    {
        public int? MinBuild { get; } = UpdateCatalog.MinBuild(Products);
    }

    /// <summary>
    /// Первая страница результатов поиска (25 строк, самые свежие сверху).
    /// null — каталог отказался искать: вместо результатов пустая страница с блоком «searchThrottled» (или вовсе без пометки).
    /// Так бывает и на частые запросы, и на «тяжёлые» — общий ID вроде «PCI\VEN_8086&amp;DEV_51E0» с тысячами записей;
    /// тот же ID с уточняющим словом при этом ищется нормально.
    /// </summary>
    public static async Task<List<Entry>?> SearchAsync(string query)
    {
        string html;
        for (int attempt = 1; ; attempt++)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var wait = _lastSearch + SearchInterval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
                html = await VendorSources.Http.GetStringAsync(Base + "Search.aspx?q=" + Uri.EscapeDataString(query)).ConfigureAwait(false);
            }
            finally
            {
                _lastSearch = DateTime.UtcNow;
                Gate.Release();
            }
            // Ответ годится, если в нём есть строки результатов или явное «ничего не найдено» (блок «noResultText»);
            // пустая страница без него — тот же отказ, что и «searchThrottled», только без пометки.
            if (!html.Contains("catalogBody_searchThrottled", StringComparison.Ordinal) &&
                (html.Contains("catalogBody_noResultText", StringComparison.Ordinal) || RowRegex.IsMatch(html))) break;
            // Дольше ждать нет смысла: «тяжёлый» запрос не пропускают и через минуты, а скан встал бы на каждом устройстве.
            if (attempt == 3) return null;
            await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 3 : 10)).ConfigureAwait(false);
        }
        var list = new List<Entry>();
        foreach (Match row in RowRegex.Matches(html))
        {
            var body = row.Groups[2].Value;
            string Cell(int c) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(
                Regex.Match(body, $@"_C{c}_R\d+""[^>]*>(.*?)</td>", RegexOptions.Singleline).Groups[1].Value, "<[^>]+>", " ")), @"\s+", " ").Trim();
            list.Add(new Entry(
                row.Groups[1].Value,
                Cell(1),
                Cell(2),
                DateTime.TryParseExact(Cell(4), "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                Version.TryParse(Cell(5), out var v) ? v : null,
                long.TryParse(Regex.Match(body, @"_originalSize"">(\d+)<").Groups[1].Value, out var s) ? s : 0));
        }
        return list;
    }

    /// <summary>
    /// С какой сборки Windows подходит драйвер — по колонке «Products»
    /// («Windows 11 Client, version 24H2 and later, Servicing Drivers», «Windows 10, Vibranium and later, …»;
    /// Windows 11 21H2 в каталоге называется «Windows - Client, version 21H2 and later»).
    /// null — продукт не распознан (серверы, Windows 7 и т. п.).
    /// </summary>
    public static int? MinBuild(string products)
    {
        int? best = null;
        foreach (Match m in Regex.Matches(products, @"Windows (10|11|-)(?: Client)?(?: S)?,\s*(?:version\s+)?([^,]+?) and later", RegexOptions.IgnoreCase))
        {
            // «Windows -» без «Client» — это «Windows - Server»: до запятой регулярка его не пропустит.
            int b = m.Groups[1].Value == "10" ? Win10(m.Groups[2].Value) : Win11(m.Groups[2].Value);
            best = best is null ? b : Math.Min(best.Value, b);
        }
        if (Regex.IsMatch(products, @"Windows 10 and later", RegexOptions.IgnoreCase)) best = Math.Min(best ?? 10240, 10240);
        return best;
    }

    private static int Win11(string release) => release.ToUpperInvariant() switch
    {
        "21H2" => 22000, "22H2" => 22621, "23H2" => 22631, "24H2" => 26100, "25H2" => 26200,
        _ => int.MaxValue // выпуск новее, чем знает программа, — не предлагаем
    };

    private static int Win10(string release)
    {
        var r = release.ToLowerInvariant();
        if (r.Contains("vibranium") || r.Contains("2004")) return 19041;
        if (r.Contains("1903") || r.Contains("may 2019") || r.Contains("19h1")) return 18362;
        if (r.Contains("1809") || r.Contains("october 2018") || r.Contains("rs5")) return 17763;
        if (r.Contains("1803") || r.Contains("april 2018")) return 17134;
        if (r.Contains("1709") || r.Contains("fall creators")) return 16299;
        if (r.Contains("1703") || r.Contains("creators")) return 15063;
        if (r.Contains("1607") || r.Contains("anniversary")) return 14393;
        return 10240;
    }

    /// <summary>
    /// Слово для уточняющего поиска под эту Windows: каталог ищет по всем словам запроса, в том числе в «Products».
    /// У 23H2 своих записей нет — ей подходят драйверы «version 22H2 and later».
    /// </summary>
    private static string ReleaseTerm(int osBuild) => osBuild switch
    {
        < 22000 => "\"Windows 10\"",
        < 22621 => "21H2",
        < 26100 => "22H2",
        < 26200 => "24H2",
        _ => "25H2"
    };

    /// <summary>
    /// Ищет драйвер для устройства: Hardware ID от самого точного к общему, первый запрос с подходящими
    /// под эту Windows результатами побеждает.
    /// </summary>
    public static async Task<DriverUpdate?> FindAsync(DeviceInfo dev, int osBuild)
    {
        // У PCI «VEN_&DEV_» без SUBSYS лежит в Compatible ID — а драйверы в каталоге чаще всего ищутся именно по нему.
        // ID по коду класса (CC_) пропускаем: они общие для всех сетевых карт, мостов и т. п.
        var queries = dev.HardwareIds.Concat(dev.CompatibleIds.Where(c => Regex.IsMatch(c, @"DEV_|PID_", RegexOptions.IgnoreCase)))
            .Select(h => h.TrimStart('*'))
            .Where(h => h.Length >= 6 && !h.Contains("CC_", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(6);
        bool answered = false;
        foreach (var q in queries)
        {
            var plain = await SearchAsync(q).ConfigureAwait(false);
            var found = (plain ?? []).Where(e => e.MinBuild is { } b && b <= osBuild).ToList();
            // Первая страница забита драйверами под свежие выпуски Windows 11 — на старой Windows подходящие
            // туда почти не попадают, а общий ID каталог может и вовсе не искать. Тогда ищем ещё раз,
            // уточнив запрос названием продукта из колонки «Products».
            // Если же каталог честно ответил «ничего не найдено», уточнять нечего — лишний запрос только приближает лимит.
            var narrowed = found.Count < MaxCandidates && plain is not [] ? await SearchAsync(q + " " + ReleaseTerm(osBuild)).ConfigureAwait(false) : null;
            found.AddRange((narrowed ?? []).Where(e => e.MinBuild is { } b && b <= osBuild && found.All(f => f.UpdateId != e.UpdateId)));
            answered |= plain is not null || narrowed is not null;
            var fit = found
                .OrderByDescending(e => e.Version).ThenByDescending(e => e.MinBuild).ThenByDescending(e => e.Date)
                .Take(MaxCandidates).ToList();
            if (fit.Count == 0) continue;
            var best = fit[0];
            return new DriverUpdate
            {
                Title = best.Title,
                Source = SourceName,
                Kind = DriverKind.Catalog,
                Version = best.Version?.ToString(),
                Date = best.Date,
                HardwareId = q,
                Manufacturer = Regex.Match(best.Title, @"^(.+?) (?:\S+ )?Driver Update").Groups[1].Value.Trim() is { Length: > 0 } m ? m : null,
                SizeBytes = best.Size,
                CatalogIds = fit.Select(e => e.UpdateId).ToArray(),
                DeviceIds = dev.HardwareIds.Concat(dev.CompatibleIds).ToArray(),
                PageUrl = Base + "Search.aspx?q=" + Uri.EscapeDataString(q)
            };
        }
        // Ни на один запрос каталог не ответил — это не «драйвера нет», а сбой, о нём надо сказать.
        if (!answered) throw new HttpRequestException("каталог временно ограничил поиск, повторите сканирование через пару минут");
        return null;
    }

    /// <summary>
    /// Ставит драйвер из каталога: по очереди пробует найденные варианты, пока не попадётся .cab,
    /// в котором есть INF для x64 с ID этого устройства.
    /// </summary>
    public static async Task<(bool ok, bool reboot, string message)> InstallAsync(DriverUpdate update, Action<string> progress)
    {
        string last = "В каталоге нет подходящего драйвера";
        foreach (var id in update.CatalogIds ?? [])
        {
            var work = CabCatalog.NewWorkDir("catalog");
            try
            {
                progress("Получаю ссылку на драйвер из каталога Microsoft…");
                var (url, sha1) = await GetFileAsync(id).ConfigureAwait(false);
                if (url is null) { last = "Каталог не дал ссылку на файл"; continue; }

                var cab = Path.Combine(work, "driver.cab");
                Directory.CreateDirectory(work);
                await DownloadAsync(url, cab, progress).ConfigureAwait(false);
                if (sha1 is not null)
                {
                    await using var s = File.OpenRead(cab);
                    if (Convert.ToBase64String(await System.Security.Cryptography.SHA1.HashDataAsync(s).ConfigureAwait(false)) != sha1)
                        return (false, false, "Контрольная сумма не совпадает с каталогом — файл повреждён или подменён");
                }
                if (VendorInstaller.CheckSignature(cab, "Microsoft") is { } sigError) return (false, false, sigError);

                progress("Распаковываю драйвер…");
                var dir = Path.Combine(work, "files");
                Directory.CreateDirectory(dir);
                using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "expand.exe"))
                       {
                           ArgumentList = { cab, "-F:*", dir },
                           CreateNoWindow = true,
                           UseShellExecute = false,
                           RedirectStandardOutput = true
                       })!)
                {
                    await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    await p.WaitForExitAsync().ConfigureAwait(false);
                }

                var (inf, hwid) = FindInf(dir, update.DeviceIds ?? [update.HardwareId!]);
                if (inf is null) { last = "В архиве нет драйвера x64 для этого устройства"; continue; }

                progress("Устанавливаю драйвер…");
                var (ok, reboot, message) = Sdi.SdiService.InstallInf(hwid!, inf);
                if (ok) return (ok, reboot, message);
                last = message;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                last = "Ошибка загрузки: " + ex.Message;
            }
            finally
            {
                CabCatalog.TryDelete(work);
            }
        }
        return (false, false, last);
    }

    /// <summary>Ссылка на .cab и его SHA-1 (base64) из окна загрузки каталога.</summary>
    private static async Task<(string? url, string? sha1)> GetFileAsync(string updateId)
    {
        var json = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]";
        using var content = new FormUrlEncodedContent([new("updateIDs", json)]);
        using var resp = await VendorSources.Http.PostAsync(Base + "DownloadDialog.aspx", content).ConfigureAwait(false);
        var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var url = Regex.Match(html, @"downloadInformation\[0\]\.files\[0\]\.url\s*=\s*'([^']+)'").Groups[1].Value;
        var sha1 = Regex.Match(html, @"downloadInformation\[0\]\.files\[0\]\.digest\s*=\s*'([^']+)'").Groups[1].Value;
        // Файлы каталога лежат только на серверах Windows Update.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.EndsWith(".windowsupdate.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase)))
            return (null, null);
        return (url, sha1.Length > 0 ? sha1 : null);
    }

    /// <summary>INF для x64, где упомянут один из ID устройства (точные ID проверяются первыми).</summary>
    private static (string? inf, string? hwid) FindInf(string dir, string[] deviceIds)
    {
        var infs = Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories)
            .Select(f => (file: f, text: File.ReadAllText(f)))
            .Where(i => i.text.Contains("NTamd64", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var id in deviceIds)
            foreach (var (file, text) in infs)
                if (Regex.IsMatch(text, @"(^|[\s,=])" + Regex.Escape(id) + @"\s*($|[,;\r\n])", RegexOptions.IgnoreCase | RegexOptions.Multiline))
                    return (file, id);
        return (null, null);
    }

    private static async Task DownloadAsync(string url, string file, Action<string> progress)
    {
        using var resp = await VendorSources.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var dst = File.Create(file);
        var buffer = new byte[1 << 20];
        long done = 0;
        int lastPercent = -1, read;
        while ((read = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            done += read;
            int percent = total > 0 ? (int)(done * 100 / total.Value) : -1;
            if (percent == lastPercent) continue;
            lastPercent = percent;
            progress(percent >= 0 ? $"Скачано {percent}% из {Math.Max(1, total!.Value / 1048576)} МБ" : $"Скачано {done / 1048576} МБ");
        }
    }
}
