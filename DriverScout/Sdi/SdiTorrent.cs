// DriverScout — модуль драйверпаков Snappy Driver Installer Origin.
// Переработка кода SDIO (update.cpp, © Glenn Delahoy и авторы SDI/SDIO), распространяется по GNU GPL v3.

using System.IO;
using MonoTorrent;
using MonoTorrent.Client;

namespace DriverScout.Sdi;

/// <summary>
/// Загрузка драйверпаков SDIO. Как и в самом SDIO, драйверпаки раздаются торрентом SDIO_Update.torrent:
/// сначала скачиваются только индексы (indexes/SDI/*.bin, несколько мегабайт), по ним подбираются драйверы,
/// и потом — только нужные архивы drivers/DP_*.7z, а не все 30+ ГБ.
/// </summary>
public sealed class SdiTorrent : IAsyncDisposable
{
    public const string TorrentUrl = "http://www.snappy-driver-installer.org/downloads/SDIO_Update.torrent";

    /// <summary>Папка драйверпаков: внутри drivers\ и indexes\SDI\, как у SDIO.</summary>
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DriverScout", "SDI");

    private ClientEngine? _engine;
    private TorrentManager? _manager;

    /// <summary>Каталог, куда торрент кладёт файлы (у торрента своя корневая папка).</summary>
    public string? ContentDir => _manager?.ContainingDirectory;

    public async Task OpenAsync()
    {
        Directory.CreateDirectory(Root);
        var torrentFile = Path.Combine(Root, "SDIO_Update.torrent");
        // Торрент обновляется вместе с драйверпаками — берём свежий, а при недоступности сайта — сохранённый.
        try
        {
            var data = await VendorSources.Http.GetByteArrayAsync(TorrentUrl).ConfigureAwait(false);
            await File.WriteAllBytesAsync(torrentFile, data).ConfigureAwait(false);
        }
        catch (Exception) when (File.Exists(torrentFile)) { }

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(Root, "cache"),
            AllowPortForwarding = true,
            AutoSaveLoadFastResume = true,
        }.ToSettings();
        _engine = new ClientEngine(settings);
        var torrent = await Torrent.LoadAsync(torrentFile).ConfigureAwait(false);
        _manager = await _engine.AddAsync(torrent, Root).ConfigureAwait(false);
    }

    /// <summary>Пути внутри торрента: "indexes/SDI/DP_LAN_Realtek_26102.bin", "drivers/DP_LAN_Realtek_26102.7z".</summary>
    public IEnumerable<string> Files => _manager!.Files.Select(f => f.Path.Replace('\\', '/'));

    public string LocalPath(string torrentPath) =>
        _manager!.Files.First(f => f.Path.Replace('\\', '/') == torrentPath).FullPath;

    /// <summary>Скачивает только указанные файлы. progress — (скачано байт, всего байт).</summary>
    public async Task DownloadAsync(IReadOnlyCollection<string> paths, Action<long, long> progress, CancellationToken ct)
    {
        var manager = _manager!;
        var wanted = manager.Files.Where(f => paths.Contains(f.Path.Replace('\\', '/'))).ToList();
        foreach (var f in manager.Files)
            await manager.SetFilePriorityAsync(f, wanted.Contains(f) ? Priority.Normal : Priority.DoNotDownload).ConfigureAwait(false);

        long total = wanted.Sum(f => f.Length);
        bool Done() => wanted.All(f => f.BitField.AllTrue);
        if (Done()) { progress(total, total); return; }

        if (manager.State is TorrentState.Stopped or TorrentState.Error)
            await manager.HashCheckAsync(false).ConfigureAwait(false); // уже скачанные куски не качаем повторно
        await manager.StartAsync().ConfigureAwait(false);
        try
        {
            while (!Done())
            {
                ct.ThrowIfCancellationRequested();
                if (manager.State == TorrentState.Error)
                    throw new InvalidOperationException("Ошибка торрента: " + manager.Error?.Exception?.Message);
                progress(wanted.Sum(f => (long)(f.BitField.PercentComplete / 100.0 * f.Length)), total);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            progress(total, total);
        }
        finally
        {
            await manager.StopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is null) return;
        await _engine.StopAllAsync().ConfigureAwait(false);
        _engine.Dispose();
    }
}
