namespace HCVault.MauiDemo;

public static class MauiProgram
{
    public static MauiApp Create()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        return builder.Build();
    }
}
