using System.Collections.ObjectModel;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using HCVault.Core;
using HCVault.Demo;

namespace HCVault.MauiDemo;

public partial class MainPage : ContentPage
{
    private sealed record FileRow(string Display);

    private readonly VolumeDemo _demo;
    private readonly ObservableCollection<string> _log = new();
    private readonly ObservableCollection<FileRow> _files = new();
    private bool _busy;

    private static readonly int[] SizeChoices = { 8, 16, 64, 256 };           // MiB
    private static readonly int[] FileSizeChoices = { 1, 10, 100, 1024 };     // KiB

    public MainPage()
    {
        InitializeComponent();

        string volumePath = Path.Combine(FileSystem.AppDataDirectory, "demo.volume");
        _demo = new VolumeDemo(volumePath);
        _demo.Logged += l => MainThread.BeginInvokeOnMainThread(() => AddLog(l));

        DeviceLabel.Text = $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model} · " +
                           $"Android API {DeviceInfo.Current.Version.Major} · {volumePath}";

        FilesView.ItemsSource = _files;
        LogView.ItemsSource = _log;

        RunButton.Clicked += async (_, _) => await BusyAsync(() => Task.Run(() => _demo.RunFullTest()), refreshFiles: false);
        CreateButton.Clicked += async (_, _) => await BusyAsync(() => Task.Run(_demo.CreateVolume), refreshFiles: false);
        OpenButton.Clicked += async (_, _) => await BusyAsync(async () =>
        {
            await Task.Run(_demo.OpenVolume);
            // mounting is optional: volumes created with Filesystem.None have
            // no filesystem — the mount error is expected then.
            try
            {
                await Task.Run(_demo.MountFilesystem);
            }
            catch (Exception ex)
            {
                AddLog(new DemoLog(DemoLogKind.Info,
                    $"filesystem not mounted ({ex.Message}) — file operations need exFAT/FAT", DateTime.UtcNow));
            }
        });
        CloseButton.Clicked += async (_, _) => await BusyAsync(() => Task.Run(_demo.Close));
        WriteButton.Clicked += async (_, _) => await BusyAsync(() =>
            Task.Run(() => _demo.WriteFile(FileNameEntry.Text?.Trim() ?? "hello.txt",
                FileSizeChoices[FileSizePicker.SelectedIndex] * 1024)));
        ReadButton.Clicked += async (_, _) => await BusyAsync(() =>
            Task.Run(() => _demo.ReadFile("\\" + (FileNameEntry.Text?.Trim() ?? "hello.txt"))));
        DeleteButton.Clicked += async (_, _) => await BusyAsync(() =>
            Task.Run(() => _demo.DeleteEntry("\\" + (FileNameEntry.Text?.Trim() ?? "hello.txt"))));
        RefreshButton.Clicked += async (_, _) => await BusyAsync(() => Task.Run(RefreshFiles), refreshFiles: false);

        RefreshInfo();
    }

    private void AddLog(DemoLog entry)
    {
        string prefix = entry.Kind switch
        {
            DemoLogKind.Ok => "OK   ",
            DemoLogKind.Fail => "FAIL ",
            _ => "info ",
        };
        _log.Insert(0, $"{prefix}{entry.Message}");
        if (_log.Count > 500)
            _log.RemoveAt(_log.Count - 1);
    }

    private void RefreshInfo()
    {
        string fs = FsPicker.SelectedIndex switch
        {
            1 => "FAT",
            2 => "None (raw)",
            _ => "exFAT",
        };
        VolumeStateLabel.Text =
            $"volume: {(_demo.VolumeFileExists ? $"{_demo.VolumeFileSize / 1024 / 1024} MiB on disk" : "not created")} · " +
            $"state: {(_demo.IsMounted ? $"open, mounted ({_demo.MountedFilesystem})" : _demo.IsOpen ? "open" : "closed")} · " +
            $"selection: {fs}";
    }

    private void RefreshFiles()
    {
        MainThread.BeginInvokeOnMainThread(() => _files.Clear());
        if (!_demo.IsMounted)
            return;
        foreach (var e in _demo.ListDirectory("\\"))
        {
            string display = e.IsDirectory
                ? $"{e.Name,-32} <dir>"
                : $"{e.Name,-32} {e.SizeBytes,12:N0} B";
            MainThread.BeginInvokeOnMainThread(() => _files.Add(new FileRow(display)));
        }
    }

    private async Task BusyAsync(Func<Task> work, bool refreshFiles = true)
    {
        if (_busy)
            return;
        _busy = true;
        SetButtonsEnabled(false);
        ApplySettings();
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            AddLog(new DemoLog(DemoLogKind.Fail, ex.Message, DateTime.UtcNow));
        }
        finally
        {
            if (refreshFiles && _demo.IsMounted)
            {
                try { RefreshFiles(); }
                catch (Exception ex) { AddLog(new DemoLog(DemoLogKind.Fail, ex.Message, DateTime.UtcNow)); }
            }
            RefreshInfo();
            SetButtonsEnabled(true);
            _busy = false;
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        RunButton.IsEnabled = enabled;
        CreateButton.IsEnabled = enabled;
        OpenButton.IsEnabled = enabled;
        CloseButton.IsEnabled = enabled;
        WriteButton.IsEnabled = enabled;
        ReadButton.IsEnabled = enabled;
        DeleteButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
    }

    // picker → VolumeDemo settings (read at the start of every operation)
    private void ApplySettings()
    {
        _demo.Filesystem = FsPicker.SelectedIndex switch
        {
            1 => VcFilesystem.Fat,
            2 => VcFilesystem.None,
            _ => VcFilesystem.ExFat,
        };
        _demo.SizeBytes = SizeChoices[Math.Max(0, SizePicker.SelectedIndex)] * 1024L * 1024;
        _demo.Password = string.IsNullOrEmpty(PasswordEntry.Text) ? "demo-passw0rd!" : PasswordEntry.Text;
    }
}
