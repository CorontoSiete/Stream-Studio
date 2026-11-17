using System.Reflection;

// Use the shipped app's framework and assemblies, rather than a second WPF host
// copied beside the probe. No product startup, user settings or visible windows.
internal static class PackageWpfProbe
{
    internal static int Verify(Assembly app)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The WPF package requires Windows.");
        Exception? failure = null;
        var rendered = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var framework = Assembly.Load("PresentationFramework");
                var applicationType = framework.GetType("System.Windows.Application", true)!;
                var application = Activator.CreateInstance(applicationType)!;
                var resources = applicationType.GetProperty("Resources")!.GetValue(application)!;
                var dictionaries = resources.GetType().GetProperty("MergedDictionaries")!.GetValue(resources)!;
                var load = applicationType.GetMethod("LoadComponent", [typeof(Uri)])!;
                foreach (var path in new[] { "Themes/StudioTheme.xaml" }.Concat(
                    new[] { "Dark", "Light", "MidnightBlue", "Dracula", "Nord", "Solarized", "Monokai", "Cyberpunk" }
                        .Select(theme => $"Themes/Colors/{theme}Colors.xaml")))
                {
                    var dictionary = load.Invoke(null, [new Uri($"/StreamStudio;component/{path}", UriKind.Relative)])!;
                    dictionaries.GetType().GetMethod("Add")!.Invoke(dictionaries, [dictionary]);
                }

                var core = Assembly.Load("PresentationCore");
                var windowsBase = Assembly.Load("WindowsBase");
                var size = Activator.CreateInstance(windowsBase.GetType("System.Windows.Size", true)!, [320d, 240d]);
                var rectangle = Activator.CreateInstance(windowsBase.GetType("System.Windows.Rect", true)!, [0d, 0d, 320d, 240d]);
                var format = core.GetType("System.Windows.Media.PixelFormats", true)!.GetProperty("Pbgra32")!.GetValue(null);
                var bitmapType = core.GetType("System.Windows.Media.Imaging.RenderTargetBitmap", true)!;
                foreach (var name in new[] { "VolumeOverlay", "ReplaySeekOverlay", "VodDownloadsView" })
                {
                    var type = app.GetType("StreamlinkVlcStudio.App.Wpf.Controls." + name, true)!;
                    var control = Activator.CreateInstance(type)!;
                    type.GetMethod("Measure")!.Invoke(control, [size]);
                    type.GetMethod("Arrange")!.Invoke(control, [rectangle]);
                    type.GetMethod("UpdateLayout")!.Invoke(control, null);
                    var bitmap = Activator.CreateInstance(bitmapType, [320, 240, 96d, 96d, format])!;
                    bitmapType.GetMethod("Render")!.Invoke(bitmap, [control]);
                    rendered++;
                }
                applicationType.GetMethod("Shutdown", Type.EmptyTypes)!.Invoke(application, null);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "Packaged WPF resource probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Packaged WPF rendering timed out.");
        if (failure is not null) throw new InvalidDataException("Packaged WPF resources could not render.", failure);
        return rendered;
    }
}
