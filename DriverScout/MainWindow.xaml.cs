using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;

namespace DriverScout;

public partial class MainWindow : Window
{
    private List<DriverRow> _rows = [];
    private ScanService? _scan;
    private ICollectionView? _view;
    private bool _busy;
    private bool _lanOffered;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        if (SystemTools.IsAdmin())
        {
            AdminButton.Visibility = Visibility.Collapsed;
            Title += " — администратор";
        }
        // Сканируем сразу при запуске: пустой экран с кнопкой ничего не даёт.
        Loaded += (_, _) => Scan_Click(this, new RoutedEventArgs());
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ShowEmpty("Сканирую…", "Ищу устройства и драйверы — это займёт около минуты");
        DeviceList.ItemsSource = null;
        try
        {
            _scan = new ScanService();
            var scan = _scan;
            _rows = await Task.Run(() => scan.Run(msg => Dispatcher.Invoke(() => StatusText.Text = msg)));
            foreach (var row in _rows)
                row.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(DriverRow.IsSelected)) UpdateInstallButton(); };

            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = o => MatchesFilter((DriverRow)o);
            DeviceList.ItemsSource = _view;

            UpdateCounts();
            ShowOem(scan.Oem);
            StatusText.Text = scan.SourceError ?? $"Готово. Можно установить: {_rows.Count(r => r.CanInstall)}";
            ReportButton.IsEnabled = true;
            RefreshEmptyState();
        }
        catch (Exception ex)
        {
            ShowEmpty("Не получилось просканировать", ex.Message);
            StatusText.Text = "";
            return;
        }
        finally
        {
            SetBusy(false);
        }
        await OfferBundledLan();
    }

    /// <summary>
    /// Сетевая карта без драйвера — значит, нет интернета и для всего остального.
    /// Встроенный драйвер предлагаем поставить сразу после сканирования, один раз за запуск.
    /// </summary>
    private async Task OfferBundledLan()
    {
        if (_lanOffered) return;
        var lan = _rows.Where(r => r.Status == RowStatus.Missing && r.Update is { Bundled: true, AutoSelect: true }).ToList();
        if (lan.Count == 0) return;
        _lanOffered = true;

        var text = "Без драйвера: " + string.Join(", ", lan.Select(r => r.Name)) + ".\n\n" +
                   "В DriverScout встроены сетевые драйверы из драйверпаков SDIO — они ставятся без интернета. " +
                   "Это сторонние сборки, не с сайта производителя.";
        if (!SystemTools.IsAdmin())
        {
            if (ThemedDialog.Ask(this, "Нет драйвера сетевой карты",
                    text + "\n\nДрайверы может устанавливать только администратор. Перезапустить DriverScout с правами администратора?",
                    "Перезапустить"))
                Admin_Click(this, new RoutedEventArgs());
            return;
        }
        if (!ThemedDialog.Ask(this, "Нет драйвера сетевой карты",
                text + "\n\nУстановить сейчас? Перед установкой создадим точку восстановления.", "Установить"))
            return;

        await InstallAsync(lan);
        if (lan.Any(r => !r.IsSelected)) // установленные строки снимаются с выбора
            ThemedDialog.Info(this, "Сетевой драйвер установлен",
                "Когда появится интернет, нажмите «Сканировать» — DriverScout найдёт остальные драйверы.");
    }

    private bool MatchesFilter(DriverRow r)
    {
        if (FilterMissing.IsChecked == true) return r.Status == RowStatus.Missing;
        if (FilterUpdate.IsChecked == true) return r.Status == RowStatus.Update;
        if (FilterProblem.IsChecked == true) return r.Status == RowStatus.Problem;
        if (FilterAll.IsChecked == true) return true;
        return r.Status != RowStatus.UpToDate; // «требуют внимания»
    }

    private void UpdateCounts()
    {
        FilterAttention.Tag = _rows.Count(r => r.Status != RowStatus.UpToDate).ToString();
        FilterMissing.Tag = _rows.Count(r => r.Status == RowStatus.Missing).ToString();
        FilterUpdate.Tag = _rows.Count(r => r.Status == RowStatus.Update).ToString();
        FilterProblem.Tag = _rows.Count(r => r.Status == RowStatus.Problem).ToString();
        FilterAll.Tag = _rows.Count.ToString();
    }

    private void ShowOem(OemInfo? oem)
    {
        if (oem is null) { OemCard.Visibility = Visibility.Collapsed; return; }
        OemTitle.Text = $"{oem.Brand} {oem.Model}";
        OemAutoChip.Visibility = oem.AutoCheck ? Visibility.Visible : Visibility.Collapsed;
        OemUtility.Text = (oem.Utility is null ? "" : $"Утилита: {oem.Utility}. ") + (oem.Remark is null ? "" : oem.Remark + ". ");
        OemLink.NavigateUri = new Uri(oem.SupportUrl);
        OemCard.Visibility = Visibility.Visible;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _view?.Refresh();
        RefreshEmptyState();
    }

    private void RefreshEmptyState()
    {
        if (_view is null) return;
        if (!_view.IsEmpty) { EmptyState.Visibility = Visibility.Collapsed; return; }
        if (FilterAttention.IsChecked == true || FilterMissing.IsChecked == true)
            ShowEmpty("Всё в порядке", "Драйверы на месте — устанавливать нечего");
        else
            ShowEmpty("Пусто", "В этой категории устройств нет");
    }

    private void ShowEmpty(string title, string subtitle)
    {
        EmptyTitle.Text = title;
        EmptySubtitle.Text = subtitle;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void UpdateInstallButton()
    {
        int n = _rows.Count(r => r.IsSelected && r.CanInstall);
        InstallButton.Content = n > 0 ? $"Установить выбранное · {n}" : "Установить выбранное";
        InstallButton.IsEnabled = !_busy && n > 0;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsSelected && r.Update is not null).ToList();
        if (selected.Count == 0) return;
        if (!SystemTools.IsAdmin())
        {
            if (ThemedDialog.Ask(this, "Нужны права администратора",
                    "Драйверы может устанавливать только администратор. Перезапустить DriverScout с правами администратора?",
                    "Перезапустить"))
                Admin_Click(sender, e);
            return;
        }

        int installers = selected.Count(r => r.Update!.Kind == DriverKind.Installer);
        int toDownload = selected.Count(r => r.Update!.Kind == DriverKind.DriverPack && Sdi.SdiService.PackFile(r.Update.PackName!) is null);
        int local = selected.Count(r => r.Update!.Kind == DriverKind.DriverPack) - toDownload;
        var message = $"Будет установлено драйверов: {selected.Count}.\n\n" +
                      (installers > 0
                          ? $"{installers} из них — установщики с сайта производителя. Они откроются в своём окне, " +
                            "там нужно будет принять лицензию.\n\n"
                          : "") +
                      (toDownload > 0
                          ? $"{toDownload} — из драйверпаков SDIO. Это сторонние сборки драйверов, их архивы " +
                            "скачаются торрентом и могут весить сотни мегабайт.\n\n"
                          : "") +
                      (local > 0
                          ? $"{local} — из драйверпаков SDIO, которые уже на диске (сетевые встроены в программу). " +
                            "Это сторонние сборки драйверов, интернет для них не нужен.\n\n"
                          : "") +
                      "Перед установкой создадим точку восстановления.";
        if (!ThemedDialog.Ask(this, "Установка драйверов", message, "Установить")) return;
        await InstallAsync(selected);
    }

    private async Task InstallAsync(List<DriverRow> selected)
    {
        var packRows = selected.Where(r => r.Update!.Kind == DriverKind.DriverPack).ToList();
        SetBusy(true);
        try
        {
            StatusText.Text = "Создаю точку восстановления…";
            var (rpOk, rpMsg) = await Task.Run(() => SystemTools.CreateRestorePoint("DriverScout: установка драйверов"));
            if (!rpOk && !ThemedDialog.Ask(this, "Без точки восстановления?", rpMsg + "\n\nПродолжить установку без неё?",
                    "Продолжить"))
                return;

            bool reboot = false;
            int done = 0, failed = 0;

            // Драйверпаки качаем заранее и одной раздачей: поиск пиров торрента занимает минуты.
            if (packRows.Count > 0)
            {
                try
                {
                    var packProgress = new Progress<string>(m => StatusText.Text = m);
                    await Task.Run(() => Sdi.SdiService.DownloadPacksAsync(packRows.Select(r => r.Update!),
                        ((IProgress<string>)packProgress).Report, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    foreach (var r in packRows) r.Result = "Не скачался драйверпак: " + ex.Message;
                    failed += packRows.Count;
                    selected = selected.Except(packRows).ToList();
                }
            }

            foreach (var row in selected)
            {
                StatusText.Text = $"Устанавливаю {done + failed + 1} из {selected.Count}: {row.Name}";
                row.Result = "Скачиваю и устанавливаю…";
                try
                {
                    bool ok;
                    if (row.Update!.Kind == DriverKind.Installer)
                    {
                        var progress = new Progress<string>(m => row.Result = m);
                        string msg;
                        (ok, msg) = await Task.Run(() => VendorInstaller.DownloadAndRunAsync(row.Update, ((IProgress<string>)progress).Report));
                        row.Result = msg;
                    }
                    else if (row.Update.Kind == DriverKind.DriverPack)
                    {
                        var progress = new Progress<string>(m => row.Result = m);
                        var (packOk, rb, msg) = await Task.Run(() => Sdi.SdiService.Install(row.Update!, ((IProgress<string>)progress).Report));
                        ok = packOk;
                        row.Result = msg;
                        reboot |= rb;
                    }
                    else if (_scan?.WindowsUpdate is { } wu)
                    {
                        var (wuOk, rb, msg) = await Task.Run(() => wu.Install(row.Update));
                        ok = wuOk;
                        row.Result = msg;
                        reboot |= rb;
                    }
                    else
                    {
                        ok = false;
                        row.Result = "Windows Update недоступен";
                    }
                    if (ok) { done++; row.IsSelected = false; } else failed++;
                }
                catch (Exception ex)
                {
                    row.Result = "Ошибка: " + ex.Message;
                    failed++;
                }
            }

            StatusText.Text = $"Установлено: {done}, ошибок: {failed}." + (reboot ? " Требуется перезагрузка." : "");
            if (reboot)
                ThemedDialog.Info(this, "Нужна перезагрузка", "Чтобы закончить установку драйверов, перезагрузите компьютер.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Sdi_Click(object sender, RoutedEventArgs e)
    {
        if (!ThemedDialog.Ask(this, "Драйверпаки SDIO",
                "DriverScout умеет подбирать драйверы из драйверпаков Snappy Driver Installer Origin — " +
                "это большая коллекция драйверов, собранная сообществом (не с сайтов производителей).\n\n" +
                "Сейчас скачаются только индексы паков (~25 МБ) через торрент — это может занять несколько минут. " +
                "Сами драйверпаки будут скачиваться только для тех драйверов, которые вы выберете к установке.",
                Sdi.SdiService.IndexDir is not null ? "Обновить индексы" : "Скачать индексы"))
            return;

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(m => StatusText.Text = m);
            await Task.Run(() => Sdi.SdiService.DownloadIndexesAsync(((IProgress<string>)progress).Report, CancellationToken.None));
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось скачать индексы SDIO: " + ex.Message;
            return;
        }
        finally
        {
            SetBusy(false);
        }
        Scan_Click(sender, e);
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"DriverScout_{DateTime.Now:yyyyMMdd_HHmm}.txt",
            Filter = "Текст (*.txt)|*.txt"
        };
        if (dlg.ShowDialog(this) == true)
            File.WriteAllText(dlg.FileName, ScanService.BuildReport(_rows, _scan?.SourceError, _scan?.Oem));
    }

    /// <summary>Уведомление о лицензии, которое GPLv3 требует показывать в интерактивных программах.</summary>
    private void About_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ThemedDialog.Info(this, "О программе",
            $"DriverScout {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} by Napster\n" +
            "Copyright © 2026 NapsterFO\n\n" +
            "Модуль драйверпаков основан на коде Snappy Driver Installer Origin " +
            "(© Glenn Delahoy, © BadPointer и другие авторы SDI/SDIO).\n\n" +
            "Это свободная программа: её можно распространять и изменять на условиях GNU General Public License " +
            "версии 3. Программа поставляется БЕЗ КАКИХ-ЛИБО ГАРАНТИЙ.\n\n" +
            "Исходный код: https://github.com/NapsterFO/DriverScout");

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (SystemTools.RestartAsAdmin()) Close();
    }

    private void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri is null) return;
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        ScanButton.IsEnabled = !busy;
        SdiButton.IsEnabled = !busy;
        DeviceList.IsEnabled = !busy;
        UpdateInstallButton();
    }
}
