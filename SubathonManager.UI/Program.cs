using Avalonia;
using SubathonManager.UI.Platform;

namespace SubathonManager.UI;

internal static class Program
{
    public static IPlatformIntegration Platform { get; } =
        OperatingSystem.IsWindows()
            ? new WindowsPlatformIntegration()
            : new PosixPlatformIntegration();
    
    [STAThread]
    public static int Main(string[] args)
    {
        // if (OperatingSystem.IsLinux())
        // {
        //     Environment.SetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        //     Environment.SetEnvironmentVariable("WEBKIT_DISABLE_COMPOSITING_MODE", "1");
        // }

        string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;
        Directory.SetCurrentDirectory(exeDir);
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { File.WriteAllText("error_load.log", $"{ex.ExceptionObject}"); } catch { /**/ }
        };
        
        if (!Platform.TryAcquireSingleInstance(args))
            return 0;

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Platform.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
