using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HCVault.Core;

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
        FileList.ItemsSource = _rows;
        Closed += (_, _) => CloseVolume();

        VolumePath.Text = OperatingSystem.IsWindows()
            ? @"C:\Volumes\demo.hc"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "demo.hc");

        Status.Text = $"Volume core ready: {HCVaultLibrary.SupportedCiphers.Count} ciphers, " +
                      $"{HCVaultLibrary.SupportedKdfs.Count} KDFs";
    }

    // ------------------------------------------------------------- helpers

    private void SetStatus(string text) => Dispatcher.UIThread.Post(() => Status.Text = text);

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
        FileList.SelectedItem is FileRow row ? row.Name : null;

    private static readonly FilePickerFileType HcFiles = new("VeraCrypt containers") { Patterns = new[] { "*.hc" } };
    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = new[] { "*" } };

    private async Task<string?> PickOpenFileAsync(string title, bool mustExist, params FilePickerFileType[] types)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

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

    private void Open_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_volume is not null)
        {
            CloseVolume();
            SetStatus("Volume closed.");
            return;
        }

        if (string.IsNullOrWhiteSpace(VolumePath.Text) || string.IsNullOrEmpty(Password!.Text))
        {
            _ = Dialogs.ShowInfoAsync(this, "Provide a volume path and a password.");
            return;
        }

        SetStatus("Opening (deriving header key)...");
        string path = VolumePath.Text!;
        string pwText = Password!.Text;
        string keyfile = KeyFileBox.Text?.Trim() ?? "";
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

                Dispatcher.UIThread.Post(() =>
                {
                    _volume = vol;
                    _fat = fat;
                    _currentDir = "\\";
                    KdfLabel.Text = vol.KdfName;
                    CipherLabel.Text = vol.CipherName;
                    HiddenLabel.Text = vol.IsHidden ? "HIDDEN VOLUME" : "";
                    OpenButton.Content = "Close";
                    RefreshListing();
                    Status.Text = $"Opened {path} — {FormatSize((long)vol.DataSize)} data area, {vol.SectorSize} B sectors.";
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Open failed: {ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------- create

    private async void Create_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(VolumePath.Text))
        {
            await Dialogs.ShowInfoAsync(this, "Set the volume path first.");
            return;
        }

        var dlg = new CreateVolumeWindow();
        if (!await dlg.ShowDialog<bool>(this))
            return;

        string path = VolumePath.Text!;
        SetStatus("Creating volume...");
        _ = Task.Run(() =>
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

    private void Up_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_fat is null || _currentDir == "\\")
            return;
        int idx = _currentDir.TrimEnd('\\').LastIndexOfAny(new[] { '\\', '/' });
        _currentDir = idx <= 0 ? "\\" : _currentDir[..(idx + 1)];
        RefreshListing();
    }

    private async void NewFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_fat is null)
            return;

        string? name = await InputDialog.ShowAsync(this, "New folder", "Folder name:", "New folder");
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

    private async void Import_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_fat is null)
            return;
        string? source = await PickOpenFileAsync("Import file into volume", mustExist: true, AllFiles);
        if (source is null)
            return;

        string target = Combine(_currentDir, Path.GetFileName(source));
        SetStatus($"Importing {source}...");
        _ = Task.Run(() =>
        {
            try
            {
                byte[] data = File.ReadAllBytes(source);
                _fat!.WriteFile(target, data);
                Dispatcher.UIThread.Post(() =>
                {
                    RefreshListing();
                    Status.Text = $"Imported {FormatSize(data.Length)} → {target}";
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Import failed: {ex.Message}");
            }
        });
    }

    private async void Export_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_fat is null)
            return;
        if (SelectedEntry() is not { } name)
        {
            Status.Text = "Select a file to export first.";
            return;
        }

        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export file from volume",
            SuggestedFileName = name,
            FileTypeChoices = new[] { AllFiles },
        });
        string? targetPath = target?.TryGetLocalPath();
        if (targetPath is null)
            return;

        try
        {
            byte[] data = _fat.ReadFile(Combine(_currentDir, name));
            File.WriteAllBytes(targetPath, data);
            Status.Text = $"Exported {name} → {targetPath} ({FormatSize(data.Length)}).";
        }
        catch (Exception ex)
        {
            Status.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_fat is null)
            return;
        if (SelectedEntry() is not { } name)
            return;

        if (!await Dialogs.ShowConfirmAsync(this, $"Delete '{name}'?", "Confirm"))
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

    private void FileList_OnDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row)
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

    private async void Browse_OnClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickOpenFileAsync("Select volume file", mustExist: false, HcFiles, AllFiles);
        if (path is not null)
            VolumePath.Text = path;
    }

    private async void KeyFile_OnClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickOpenFileAsync("Select key file (any file works)", mustExist: true, AllFiles);
        if (path is not null)
            KeyFileBox.Text = path;
    }
}
