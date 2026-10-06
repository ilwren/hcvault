using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HCVault.Core;

namespace HCVault.Explorer;

public partial class CreateVolumeWindow : Window
{
    public long SizeBytes { get; private set; }
    public string PasswordText { get; private set; } = "";
    public int Pim { get; private set; }
    public string KeyFilePath { get; private set; } = "";
    public VcCipher Cipher { get; private set; } = VcCipher.Aes;
    public VcKdf Kdf { get; private set; } = VcKdf.Argon2id;
    public VcFilesystem Filesystem { get; private set; } = VcFilesystem.Fat;

    public CreateVolumeWindow() => InitializeComponent();

    private async void BrowseKey_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select key file (any file works)",
            AllowMultiple = false,
        });
        if (files.Count > 0)
            KeyFileBox.Text = files[0].TryGetLocalPath() ?? "";
    }

    private async void Create_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!long.TryParse(SizeBox.Text, out long mb) || mb < 1)
        {
            await Dialogs.ShowInfoAsync(this, "Enter a size of at least 1 MB.", "Create volume");
            return;
        }

        if (string.IsNullOrEmpty(PwBox!.Text))
        {
            await Dialogs.ShowInfoAsync(this, "Password must not be empty.", "Create volume");
            return;
        }

        if (PwBox!.Text != PwBox2!.Text)
        {
            await Dialogs.ShowInfoAsync(this, "Passwords do not match.", "Create volume");
            return;
        }

        SizeBytes = mb * 1024 * 1024;
        PasswordText = PwBox!.Text;
        Pim = int.TryParse(PimBox.Text, out var p) ? p : 0;
        KeyFilePath = KeyFileBox.Text?.Trim() ?? "";
        Cipher = (VcCipher)CipherBox.SelectedItem!;
        Kdf = (VcKdf)KdfBox.SelectedItem!;
        Filesystem = FsBox.SelectedIndex switch
        {
            1 => VcFilesystem.ExFat,
            2 => VcFilesystem.None,
            _ => VcFilesystem.Fat,
        };

        Close(true);
    }
}
