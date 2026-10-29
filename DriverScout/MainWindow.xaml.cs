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
        }
        finally
        {
            SetBusy(false);
        }
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
        var message = $"Будет установлено драйверов: {selected.Count}.\n\n" +
                      (installers > 0
                          ? $"{installers} из них — установщики с сайта производителя. Они откроются в своём окне, " +
                            "там нужно будет принять лицензию.\n\n"
                          : "") +
                      "Перед установкой создадим точку восстановления.";
        if (!ThemedDialog.Ask(this, "Установка драйверов", message, "Установить")) return;

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
        DeviceList.IsEnabled = !busy;
        UpdateInstallButton();
    }
}
