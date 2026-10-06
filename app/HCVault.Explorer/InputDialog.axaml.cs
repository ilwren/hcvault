using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HCVault.Explorer;

public partial class InputDialog : Window
{
    public string? Value { get; private set; }

    public InputDialog() => InitializeComponent();

    public static async Task<string?> ShowAsync(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog { Title = title };
        dlg.Prompt.Text = prompt;
        dlg.Input.Text = initial;
        dlg.Input.SelectAll();
        return (await dlg.ShowDialog<string?>(owner)) is not null ? dlg.Value : null;
    }

    private void Ok_OnClick(object? sender, RoutedEventArgs e)
    {
        Value = Input.Text;
        Close(Value);
    }
}
