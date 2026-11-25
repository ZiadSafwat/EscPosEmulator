using Avalonia;
using EscPosEmulator.Core;

namespace EscPosEmulator;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Register legacy code pages (CP437, CP850, ...) BEFORE any use.
        // Without this, .NET 5+ throws:
        //   System.NotSupportedException: No data is available for encoding 437.
        EncodingHelper.EnsureRegistered();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseSkia();
}
