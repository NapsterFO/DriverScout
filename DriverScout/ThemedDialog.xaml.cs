using System.Windows;
using System.Windows.Input;

namespace DriverScout;

/// <summary>Диалог в стиле приложения вместо системного MessageBox.</summary>
public partial class ThemedDialog : Window
{
    private ThemedDialog(Window owner, string title, string message, string ok, string? cancel)
    {
        InitializeComponent();
        Owner = owner;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = ok;
        if (cancel is null) CancelButton.Visibility = Visibility.Collapsed;
        else CancelButton.Content = cancel;
    }

    /// <summary>Вопрос с двумя кнопками. true — пользователь нажал основную.</summary>
    public static bool Ask(Window owner, string title, string message, string ok = "Продолжить", string cancel = "Отмена") =>
        new ThemedDialog(owner, title, message, ok, cancel).ShowDialog() == true;

    /// <summary>Сообщение с одной кнопкой.</summary>
    public static void Info(Window owner, string title, string message) =>
        new ThemedDialog(owner, title, message, "Понятно", null).ShowDialog();

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
