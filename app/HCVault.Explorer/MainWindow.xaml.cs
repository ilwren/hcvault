using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using HCVault.Core;
using HCVault.Core.Fat;

namespace HCVault.Explorer;

public record FileRow(string Name, string Type, string Size, string Modified);

public partial class MainWindow : Window
{
    private Volume? _volume;
    private IVolumeFileSystem? _fat;
    private string _currentDir = "\\";
    private readonly ObservableCollection<FileRow> _rows = new();

    public MainWindow()
    {
        InitializeComponent();
        FileGrid.ItemsSource = _rows;
        Closed += (_, _) => CloseVolume();
        Status.Text = $"Volume core ready: {HCVaultLibrary.SupportedCiphers.Count} ciphers, " +
                      $"{HCVaultLibrary.SupportedKdfs.Count} KDFs";
    }

    // ------------------------------------------------------------- helpers

    private void SetStatus(string text) => Dispatcher.BeginInvoke((Action)(() => Status.Text = text));

    private void RefreshListing()
    {
        if (_fat is null)
            return;

        _rows.Clear();
        foreach (var e in _fat.ListDirectory(_currentDir))
        {
            _rows.Add(new FileRow(
                e.Name,
                e.IsDirectory ? "DIR" : "FILE",
                e.IsDirectory ? "" : FormatSize(e.SizeBytes),
                e.ModifiedUtc.ToString("u")));
        }
        CurrentDir.Text = _currentDir;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1 << 30):F2} GB",
        >= 1 << 20 => $"{bytes / (double)(1 << 20):F2} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):F1} KB",
        _ => $"{bytes} B",
    };

    private static string Combine(string dir, string name) =>
        dir == "\\" ? $"\\{name}" : $"{dir.TrimEnd('\\')}\\{name}";

    private string? SelectedEntry() =>
        FileGrid.SelectedItem is FileRow row ? row.Name : null;

    // --------------------------------------------------------------- open

    private void CloseVolume()
    {
        _fat?.Dispose();
        _fat = null;
        _volume?.Dispose();
        _volume = null;
        KdfLabel.Text = "-";
        CipherLabel.Text = "-";
        HiddenLabel.Text = "";
        _rows.Clear();
        OpenButton.Content = "Open / Close";
    }

    private void Open_OnClick(object sender, RoutedEventArgs e)
    {
        if (_volume is not null)
        {
            CloseVolume();
            SetStatus("Volume closed.");
            return;
        }

        if (string.IsNullOrWhiteSpace(VolumePath.Text) || Password.Password.Length == 0)
        {
            MessageBox.Show("Provide a volume path and a password.", "HCVault Explorer");
            return;
        }

        SetStatus("Opening (deriving header key)...");
        string path = VolumePath.Text;
        string pwText = Password.Password;
        string keyfile = KeyFileBox.Text.Trim();
        int pim = int.TryParse(PimBox.Text, out var p) ? p : 0;

        Task.Run(() =>
        {
            try
            {
                using var pw = SecurePassword.FromText(pwText);
                var vol = Volume.Open(new VolumeOpenOptions
                {
                    Path = path,
                    Password = pw,
                    Pim = pim,
                    KeyFiles = keyfile.Length > 0 ? new[] { keyfile } : Array.Empty<string>(),
                });
                var fat = VolumeFilesystem.Mount(vol); // auto-detects FAT / exFAT

                Dispatcher.BeginInvoke((Action)(() =>
                {
                    _volume = vol;
                    _fat = fat;
                    _currentDir = "\\";
                    KdfLabel.Text = vol.KdfName;
                    CipherLabel.Text = vol.CipherName;
                    HiddenLabel.Text = vol.IsHidden ? "HIDDEN VOLUME" : "";
                    OpenButton.Content = "Close";
                    RefreshListing();
                    Status.Text = $"Opened {path} — {FormatSize(vol.DataSize)} data area, {vol.SectorSize} B sectors.";
                }));
            }
            catch (Exception ex)
            {
                SetStatus($"Open failed: {ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------- create

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(VolumePath.Text))
        {
            MessageBox.Show("Set the volume path first.", "HCVault Explorer");
            return;
        }

        var dlg = new CreateVolumeWindow { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        string path = VolumePath.Text;
        SetStatus("Creating volume...");
        Task.Run(() =>
        {
            try
            {
                using var pw = SecurePassword.FromText(dlg.PasswordText);
                Volume.Create(new VolumeCreationOptions
                {
                    Path = path,
                    SizeBytes = dlg.SizeBytes,
                    Password = pw,
                    Pim = dlg.Pim,
                    KeyFiles = dlg.KeyFilePath is { Length: > 0 } ? new[] { dlg.KeyFilePath } : Array.Empty<string>(),
                    Cipher = dlg.Cipher,
                    Kdf = dlg.Kdf,
                    Filesystem = dlg.Filesystem,
                    Quick = true,
                    Progress = p => SetStatus($"Creating... {p.Fraction:P0} ({p.Stage})"),
                });
                SetStatus($"Volume created: {path}");
            }
            catch (Exception ex)
            {
                SetStatus($"Create failed: {ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------ actions

    private void Up_OnClick(object sender, RoutedEventArgs e)
    {
        if (_fat is null || _currentDir == "\\")
            return;
        int idx = _currentDir.TrimEnd('\\').LastIndexOfAny(new[] { '\\', '/' });
        _currentDir = idx <= 0 ? "\\" : _currentDir[..(idx + 1)];
        RefreshListing();
    }

    private void NewFolder_OnClick(object sender, RoutedEventArgs e)
    {
        if (_fat is null)
            return;

        string? name = InputDialog.Show("New folder", "Folder name:", "New folder");
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            _fat.CreateDirectory(Combine(_currentDir, name));
            RefreshListing();
            Status.Text = $"Folder '{name}' created.";
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }

    private void Import_OnClick(object sender, RoutedEventArgs e)
    {
        if (_fat is null)
            return;
        var dlg = new OpenFileDialog { Title = "Import file into volume" };
        if (dlg.ShowDialog() != true)
            return;

        string target = Combine(_currentDir, Path.GetFileName(dlg.FileName));
        SetStatus($"Importing {dlg.FileName}...");
        Task.Run(() =>
        {
            try
            {
                byte[] data = File.ReadAllBytes(dlg.FileName);
                _fat!.WriteFile(target, data);
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    RefreshListing();
                    Status.Text = $"Imported {FormatSize(data.Length)} → {target}";
                }));
            }
            catch (Exception ex)
            {
                SetStatus($"Import failed: {ex.Message}");
            }
        });
    }

    private void Export_OnClick(object sender, RoutedEventArgs e)
    {
        if (_fat is null)
            return;
        if (SelectedEntry() is not { } name)
        {
            Status.Text = "Select a file to export first.";
            return;
        }

        var dlg = new SaveFileDialog { FileName = name };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            byte[] data = _fat.ReadFile(Combine(_currentDir, name));
            File.WriteAllBytes(dlg.FileName, data);
            Status.Text = $"Exported {name} → {dlg.FileName} ({FormatSize(data.Length)}).";
        }
        catch (Exception ex)
        {
            Status.Text = $"Export failed: {ex.Message}";
        }
    }

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_fat is null)
            return;
        if (SelectedEntry() is not { } name)
            return;

        if (MessageBox.Show(this, $"Delete '{name}'?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        try
        {
            _fat.Delete(Combine(_currentDir, name));
            RefreshListing();
            Status.Text = $"Deleted {name}.";
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }

    private void FileGrid_OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileGrid.SelectedItem is not FileRow row)
            return;

        if (row.Type == "DIR")
        {
            _currentDir = Combine(_currentDir, row.Name);
            RefreshListing();
        }
        else
        {
            Status.Text = $"Tip: use 'Export file...' to save '{row.Name}' ({row.Size}).";
        }
    }

    // ----------------------------------------------------------- plumbing

    private void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "VeraCrypt containers (*.hc)|*.hc|All files (*.*)|*.*",
            CheckFileExists = false,
        };
        if (dlg.ShowDialog() == true)
            VolumePath.Text = dlg.FileName;
    }

    private void KeyFile_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select key file (any file works)" };
        if (dlg.ShowDialog() == true)
            KeyFileBox.Text = dlg.FileName;
    }
}
