using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DriverScout;

/// <summary>Вкладка BIOS: установленная версия и версии у производителя.</summary>
public partial class BiosPage : UserControl
{
    private bool _loaded;
    private string? _page;

    public BiosPage() => InitializeComponent();

    public async Task ActivateAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await CheckAsync();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        CheckButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Verdict.Text = "Проверяю сайт производителя…";
        try
        {
            var system = await Pc.ProfileAsync();
            if (system is null)
            {
                Verdict.Text = "Не удалось прочитать сведения о компьютере (WMI).";
                return;
            }
            var report = await Task.Run(() => BiosService.CheckAsync(system));
            Show(report);
        }
        catch (Exception ex)
        {
            Verdict.Text = "Не получилось проверить BIOS: " + ex.Message;
        }
        finally
        {
            CheckButton.IsEnabled = true;
            Progress.Visibility = Visibility.Hidden;
        }
    }

    private void Show(BiosReport r)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        InstalledVersion.Text = r.Installed.Version.Length > 0 ? r.Installed.Version : "неизвестно";
        InstalledDate.Text = r.Installed.Date is { } d ? "от " + d.ToString("d MMMM yyyy", ru) : "";
        DeviceText.Text = Or(r.Installed.Device);
        BoardText.Text = Or(r.Installed.Board);
        VendorText.Text = Or(r.Installed.Vendor);

        _page = r.PageUrl;
        PageButton.Visibility = _page is null ? Visibility.Collapsed : Visibility.Visible;
        Releases.ItemsSource = r.Releases;
        ListTitle.Visibility = r.Releases.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VerdictCard.BorderBrush = System.Windows.Media.Brushes.Transparent;

        if (r.Latest is { } latest)
        {
            LatestLabel.Text = $"ПОСЛЕДНЯЯ У ПРОИЗВОДИТЕЛЯ · {r.SourceName}";
            LatestVersion.Text = latest.Version;
            LatestDate.Text = latest.Date is null ? "" : "от " + latest.DateText;
            LatestVersion.Foreground = (System.Windows.Media.Brush)FindResource(latest.IsNewer == true ? "AccentBrush" : "TextBrush");
            var newer = r.Releases.Count(x => x.IsNewer == true);
            Verdict.Text = latest.IsNewer switch
            {
                true => $"Есть обновление BIOS: {(newer == 1 ? "вышла 1 новая версия" : $"вышло новых версий: {newer}")}. " +
                        "Прочитайте описание на странице поддержки — не каждое обновление BIOS нужно ставить.",
                false => "Установлена последняя версия BIOS. Обновлять нечего.",
                null => "Производитель записывает версии иначе, чем BIOS компьютера, — сравните вручную по списку ниже."
            };
            if (latest.IsNewer == true) VerdictCard.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        else
        {
            LatestLabel.Text = "ПОСЛЕДНЯЯ У ПРОИЗВОДИТЕЛЯ";
            LatestVersion.Text = "—";
            LatestDate.Text = "";
            Verdict.Text = r.Error is not null
                ? $"Не получилось получить список BIOS: {r.Error}."
                : "Сайт этого производителя не отдаёт список BIOS программам (автоматически DriverScout проверяет ASUS, Lenovo, Dell и HP). " +
                  "Последняя версия — на странице вашей модели: сравните её с установленной слева.";
        }
    }

    private static string Or(string s) => s.Length > 0 ? s : "—";

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        if (_page is not null) Process.Start(new ProcessStartInfo(_page) { UseShellExecute = true });
    }

    /// <summary>Файл BIOS открываем в браузере: прошивать его всё равно утилитой производителя.</summary>
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BiosRelease { DownloadUrl: { } url })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
