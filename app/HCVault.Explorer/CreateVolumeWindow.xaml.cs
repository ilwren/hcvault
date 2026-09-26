using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
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

    private void BrowseKey_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select key file (any file works)" };
        if (dlg.ShowDialog() == true)
            KeyFileBox.Text = dlg.FileName;
    }

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(SizeBox.Text, out long mb) || mb < 1)
        {
            MessageBox.Show(this, "Enter a size of at least 1 MB.", "Create volume");
            return;
        }

        if (PwBox.Password.Length == 0)
        {
            MessageBox.Show(this, "Password must not be empty.", "Create volume");
            return;
        }

        if (PwBox.Password != PwBox2.Password)
        {
            MessageBox.Show(this, "Passwords do not match.", "Create volume");
            return;
        }

        SizeBytes = mb * 1024 * 1024;
        PasswordText = PwBox.Password;
        Pim = int.TryParse(PimBox.Text, out var p) ? p : 0;
        KeyFilePath = KeyFileBox.Text.Trim();
        Cipher = (VcCipher)CipherBox.SelectedItem;
        Kdf = (VcKdf)KdfBox.SelectedItem;
        Filesystem = FsBox.SelectedIndex switch
        {
            1 => VcFilesystem.ExFat,
            2 => VcFilesystem.None,
            _ => VcFilesystem.Fat,
        };

        DialogResult = true;
    }
}
