using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DriverScout;

/// <summary>Вкладка «Для геймеров»: лаунчеры, общение, программы для видеокарты и утилиты производителей.</summary>
public partial class GamerPage : UserControl
{
    private const string AllCategories = "Все";
    private readonly List<GameApp> _apps = GameApps.Create();
    private readonly ICollectionView _view;
    /// <summary>winget и установщики по очереди: два установщика сразу мешают друг другу.</summary>
    private readonly SemaphoreSlim _queue = new(1, 1);
    private string _category = AllCategories;
    private bool _loaded;

    public GamerPage()
    {
        InitializeComponent();
        _view = new ListCollectionView(_apps);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GameApp.Category)));
        _view.Filter = o => Matches((GameApp)o);
        AppList.ItemsSource = _view;

        foreach (var c in new[] { AllCategories }.Concat(_apps.Select(a => a.Category).Distinct()))
        {
            var pill = new RadioButton { Content = c, Style = (Style)FindResource("PillToggle"), GroupName = "Category", IsChecked = c == AllCategories };
            pill.Checked += (_, _) => { _category = c; _view.Refresh(); };
            Categories.Children.Add(pill);
        }
    }

    /// <summary>Проверяет, что уже установлено, — при первом открытии вкладки.</summary>
    public async Task ActivateAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshInstalledAsync();
    }

    private async Task RefreshInstalledAsync()
    {
        var system = await Pc.ProfileAsync();
        await Task.Run(() => GameApps.Refresh(_apps, system));
        int installed = _apps.Count(a => a.IsInstalled);
        Subtitle.Text = $"Всё, что нужно игровому компьютеру, — ставится прямо отсюда · установлено {installed} из {_apps.Count}";
    }

    private bool Matches(GameApp a) =>
        (_category == AllCategories || a.Category == _category) &&
        (Search.Text.Trim() is not { Length: > 0 } q
         || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(q, StringComparison.OrdinalIgnoreCase));

    private void Search_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameApp app) return;
        if (app.Source != AppSource.Page && app.IsInstalled &&
            !ThemedDialog.Ask(Window.GetWindow(this), app.Name, $"{app.Name} уже установлен. Установить заново поверх?", "Переустановить"))
            return;

        app.IsBusy = true;
        app.Status = "В очереди…";
        await _queue.WaitAsync();
        try
        {
            app.Status = "Готовлю установку…";
            var progress = new Progress<string>(m => app.Status = m);
            var (ok, message) = await Task.Run(() => GameApps.InstallAsync(app, ((IProgress<string>)progress).Report));
            app.Status = (ok ? "✓ " : "") + message;
        }
        catch (Exception ex)
        {
            app.Status = "Ошибка: " + ex.Message;
        }
        finally
        {
            _queue.Release();
            app.IsBusy = false;
        }
        await RefreshInstalledAsync();
    }
}
