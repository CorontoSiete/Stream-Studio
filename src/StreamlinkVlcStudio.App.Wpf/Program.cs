using System.Runtime.CompilerServices;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The updater stages only this executable. Dispatch command modes before
        // constructing Application or loading XAML, which require installed WPF DLLs.
        if (MaintenanceModeRunner.TryRun(args, out var exitCode) ||
            UpdateHelperRunner.TryRun(args, out exitCode))
            return exitCode;

        return RunApplication();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
