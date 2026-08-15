using System.Windows;

namespace SplitVpn.Views;

public partial class TextInputWindow : Wpf.Ui.Controls.FluentWindow
{
    public TextInputWindow(string title, string prompt, bool multiline)
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

        Loaded += (_, _) => InputBox.Focus();
    }

    public string Value => InputBox.Text;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
