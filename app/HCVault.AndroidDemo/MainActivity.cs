using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using HCVault.Demo;

namespace HCVault.AndroidDemo;

/// <summary>
///   Plain-Android UI (no MAUI) so the volume demo also runs on Android 5.0
///   (API 21). One button runs the full lifecycle test; every step reports
///   OK/FAIL into the on-screen log.
/// </summary>
[Activity(Label = "HCVault demo (API 21+)", MainLauncher = true)]
public class MainActivity : Activity
{
    private VolumeDemo _demo = null!;
    private Button _runButton = null!;
    private TextView _statusView = null!;
    private TextView _logView = null!;
    private ScrollView _scroller = null!;

    private int Dp(int v) => (int)(v * Resources!.DisplayMetrics.Density);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        string volumePath = System.IO.Path.Combine(FilesDir!.AbsolutePath, "demo.volume");
        _demo = new VolumeDemo(volumePath);
        _demo.Logged += log => RunOnUiThread(() => AppendLog(log));

        // --------------------------------------------- UI (programmatic)
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.ParseColor("#12121C"));

        var title = new TextView(this) { Text = "HCVault · Android demo", TextSize = 20f };
        title.SetTextColor(Color.White);
        title.SetPadding(Dp(16), Dp(14), Dp(16), 0);

        var device = new TextView(this) { TextSize = 12f };
        device.SetTextColor(Color.ParseColor("#9A9AB0"));
        device.SetPadding(Dp(16), 0, Dp(16), Dp(8));
        device.Text = $"{Build.Manufacturer} {Build.Model} · Android API {(int)Build.VERSION.SdkInt} · " +
                      $"volume: {volumePath}";

        _statusView = new TextView(this) { TextSize = 13f };
        _statusView.SetTextColor(Color.ParseColor("#9A9AB0"));
        _statusView.SetPadding(Dp(16), 0, Dp(16), Dp(8));
        _statusView.Text = "idle — tap the button to run the full volume lifecycle test";

        _runButton = new Button(this) { Text = "RUN FULL TEST" };
        _runButton.SetTextColor(Color.White);
        _runButton.Click += (_, _) => OnRunClicked();

        _logView = new TextView(this) { TextSize = 12f };
        _logView.SetTextColor(Color.ParseColor("#EDEDF5"));
        _logView.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        _logView.SetPadding(Dp(10), Dp(10), Dp(10), Dp(10));

        _scroller = new ScrollView(this);
        _scroller.AddView(_logView);
        _scroller.SetBackgroundColor(Color.ParseColor("#1E1E2E"));

        root.AddView(title);
        root.AddView(device);
        root.AddView(_statusView);
        root.AddView(_runButton);
        root.AddView(_scroller, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        SetContentView(root);
    }

    private void AppendLog(DemoLog log)
    {
        string prefix = log.Kind switch
        {
            DemoLogKind.Ok => "OK   ",
            DemoLogKind.Fail => "FAIL ",
            _ => "info ",
        };
        _logView.Append(prefix + log.Message + "\n");
        _scroller.Post(() => _scroller.FullScroll(FocusSearchDirection.Down));
    }

    private void OnRunClicked()
    {
        if (!_runButton.Enabled)
            return;
        _runButton.Enabled = false;
        _runButton.Text = "running…";
        _statusView.Text = "create → wrong password rejected → open → mount → write → read back → verify → reopen";
        _logView.Text = "";

        System.Threading.Tasks.Task.Run(() =>
        {
            bool pass;
            try
            {
                pass = _demo.RunFullTest();
            }
            catch (Exception ex)
            {
                RunOnUiThread(() => AppendLog(new DemoLog(DemoLogKind.Fail, ex.Message, DateTime.UtcNow)));
                pass = false;
            }

            RunOnUiThread(() =>
            {
                _runButton.Enabled = true;
                _runButton.Text = pass ? "FULL TEST PASSED — RUN AGAIN" : "FAILED — RETRY";
                _statusView.Text = pass
                    ? "every step OK (exFAT volume created, opened and verified on this device)"
                    : "one or more steps failed — see log above";
            });
        });
    }
}
