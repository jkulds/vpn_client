using System.Windows;

namespace SplitVpn.Views;

public partial class SubscriptionWindow : Wpf.Ui.Controls.FluentWindow
{
    public SubscriptionWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string SubscriptionName => string.IsNullOrWhiteSpace(NameBox.Text) ? "Подписка" : NameBox.Text.Trim();
    public string SubscriptionUrl => UrlBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            MessageBox.Show("Укажите URL подписки.", "Новая подписка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
