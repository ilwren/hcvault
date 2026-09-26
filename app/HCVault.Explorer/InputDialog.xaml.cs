using System.Windows;
using System.Windows.Controls;

namespace HCVault.Explorer;

public partial class InputDialog : Window
{
    public string? Value { get; private set; }

    public InputDialog() => InitializeComponent();

    public static string? Show(string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog { Title = title };
        dlg.Prompt.Text = prompt;
        dlg.Input.Text = initial;
        dlg.Input.SelectAll();
        dlg.Input.Focus();
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        Value = Input.Text;
        DialogResult = true;
    }
}
