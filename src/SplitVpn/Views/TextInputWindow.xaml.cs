using System.Windows;

namespace SplitVpn.Views;

public partial class TextInputWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <param name="secondaryLabel">
    /// Подпись второго, однострочного поля. null - поля нет. Нужно импорту: туда вводится
    /// группа, под которой серверы появятся в списке.
    /// </param>
    public TextInputWindow(
        string title,
        string prompt,
        bool multiline,
        string? secondaryLabel = null,
        string? secondaryPlaceholder = null)
    {
        InitializeComponent();

        Title = title;
        Bar.Title = title;
        PromptText.Text = prompt;
        InputBox.AcceptsReturn = multiline;

        if (!multiline)
        {
            InputBox.MaxHeight = 34;
            InputBox.VerticalAlignment = VerticalAlignment.Top;
            Height = 260;
        }

        if (secondaryLabel is not null)
        {
            SecondaryPanel.Visibility = Visibility.Visible;
            SecondaryLabel.Text = secondaryLabel;
            SecondaryBox.PlaceholderText = secondaryPlaceholder ?? "";
        }

        Loaded += (_, _) => InputBox.Focus();
    }

    public string Value => InputBox.Text;

    /// <summary>Содержимое второго поля; пусто, если поле не показывалось или не заполнено.</summary>
    public string? SecondaryValue =>
        SecondaryPanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(SecondaryBox.Text)
            ? SecondaryBox.Text.Trim()
            : null;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
