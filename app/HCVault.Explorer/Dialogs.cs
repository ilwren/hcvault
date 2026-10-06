using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HCVault.Explorer;

/// <summary>
///   Minimal message boxes (Avalonia ships none) - small code-built windows,
///   reflection-free so they are Native-AOT safe.
/// </summary>
internal static class Dialogs
{
    public static Task ShowInfoAsync(Window owner, string text, string title = "HCVault Explorer")
        => ShowAsync(owner, text, title, yesNo: false);

    public static Task<bool> ShowConfirmAsync(Window owner, string text, string title = "Confirm")
        => ShowAsync(owner, text, title, yesNo: true);

    private static async Task<bool> ShowAsync(Window owner, string text, string title, bool yesNo)
    {
        bool result = false;

        var message = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
        };

        var ok = new Button { Content = yesNo ? "Yes" : "OK", Width = 76, IsDefault = true };
        var cancel = new Button { Content = yesNo ? "No" : "Cancel", Width = 76, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { ok, cancel },
        };

        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(16), Children = { message, buttons } },
        };

        ok.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return result;
    }
}
