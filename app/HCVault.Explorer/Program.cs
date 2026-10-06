using Avalonia;

namespace HCVault.Explorer;

internal static class Program
{
    // Desktop entry point. With PublishAot (see the .csproj) this builds into
    // a single native executable per platform.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
