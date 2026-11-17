using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;

// Run only by the packaging smoke test, before the actual app entry point.
// Inspect the shipped executable's default updater and optionally exercise its
// signed release check and download against GitHub, then exit before opening UI.
internal static class StartupHook
{
    public static void Initialize()
    {
        var restartExecutable = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_RESTART_EXECUTABLE");
        // The real helper passes its environment through Setup to the relaunched app.
        // Let every other process and installer maintenance invocation run normally;
        // only inspect the final app launch, which has no command-line arguments.
        if (!string.IsNullOrEmpty(restartExecutable) &&
            (!string.Equals(Environment.ProcessPath, restartExecutable, StringComparison.OrdinalIgnoreCase) ||
                Environment.GetCommandLineArgs().Length != 1))
            return;

        try
        {
            var assembly = Assembly.Load("StreamlinkVlcStudio.Infrastructure");
            var type = assembly.GetType("StreamlinkVlcStudio.Infrastructure.Updates.StagedAppUpdateService", true)!;
            var loggerType = assembly.GetType("StreamlinkVlcStudio.Infrastructure.Logging.FileAppLogger", true)!;
            using var logger = (IDisposable)Activator.CreateInstance(loggerType,
                new object?[] { Path.Combine(Path.GetTempPath(), "StreamStudio-update-probe", Guid.NewGuid().ToString("N")) })!;
            using var service = (IDisposable)Activator.CreateInstance(type, new object?[] { logger })!;
            var directory = (string)type.GetField("applicationDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var detect = type.GetMethod("DetectInstallKind", BindingFlags.Static | BindingFlags.NonPublic)!;
            object? liveUpdate = null;
            var expectedTag = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_EXPECTED_TAG");
            if (!string.IsNullOrEmpty(expectedTag))
            {
                var checkOnly = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_CHECK_ONLY") == "true";
                var expectedKind = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_EXPECTED_INSTALL_KIND") ?? "Managed";
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                if (Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_CONSUME_COMPLETION") == "true")
                {
                    var completion = (Task)type.GetMethod("ConsumeCompletionAsync")!.Invoke(service, [cancellation.Token])!;
                    completion.GetAwaiter().GetResult();
                }
                var checkMethod = type.GetMethod("CheckAsync")!;
                var reason = Enum.Parse(checkMethod.GetParameters()[0].ParameterType, "Manual");
                var check = AwaitResult(checkMethod.Invoke(service, [reason, cancellation.Token])!);
                var release = ReadProperty(check, "Release");
                if (ReadProperty(check, "Checked") is not true ||
                    (!checkOnly && (ReadProperty(check, "IsNotifyOnly") is not false ||
                        ReadProperty(check, "InstallKind").ToString() != expectedKind)) ||
                    ReadProperty(release, "Tag").ToString() != expectedTag)
                    throw new InvalidOperationException("The installed updater did not authenticate the expected release and installation type.");

                var download = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_DOWNLOAD") == "true";
                object? prepared = null;
                if (download)
                {
                    if (ReadProperty(check, "IsUpdateAvailable") is not true)
                        throw new InvalidOperationException("The installed updater did not detect the published upgrade.");
                    prepared = AwaitResult(type.GetMethod("DownloadAsync")!.Invoke(service,
                        [release, null, cancellation.Token])!);
                }
                else if (!checkOnly && ReadProperty(check, "IsUpdateAvailable") is not false)
                    throw new InvalidOperationException("The upgraded application still offers an update to its installed version.");

                var phase = ReadProperty(ReadProperty(service, "State"), "Phase").ToString();
                if (!checkOnly && phase != (download ? "Ready" : "Completed"))
                    throw new InvalidOperationException($"The published updater ended in unexpected phase {phase}.");
                object? launch = null;
                if (Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_APPLY") == "true")
                {
                    if (!download || checkOnly || prepared is null)
                        throw new InvalidOperationException("The upgrade probe must download and verify an update before applying it.");
                    Environment.SetEnvironmentVariable("SVS_UPDATE_PROBE_DOWNLOAD", "false");
                    Environment.SetEnvironmentVariable("SVS_UPDATE_PROBE_APPLY", "false");
                    Environment.SetEnvironmentVariable("SVS_UPDATE_PROBE_CONSUME_COMPLETION", "true");
                    launch = AwaitResult(type.GetMethod("ApplyAndRestartAsync")!.Invoke(service, [prepared, cancellation.Token])!);
                    if (ReadProperty(launch, "Started") is not true)
                        throw new InvalidOperationException("The real update helper did not start.");
                }
                liveUpdate = new { Check = check, PreparedUpdate = prepared, Phase = phase, Launch = launch };
            }
            var report = JsonSerializer.Serialize(new
            {
                ProcessPath = Environment.ProcessPath,
                BaseDirectory = AppContext.BaseDirectory,
                ConfiguredDirectory = directory,
                InstallKind = detect.Invoke(null, [directory])!.ToString(),
                LiveUpdate = liveUpdate,
                Package = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_PACKAGE_RESOURCES") == "true"
                    ? ProbePackage() : null
            });
            var restartReport = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_RESTART_REPORT");
            if (!string.IsNullOrEmpty(restartReport))
            {
                File.WriteAllText(restartReport + ".tmp", report);
                File.Move(restartReport + ".tmp", restartReport, overwrite: true);
            }
            Console.WriteLine(report);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
        Environment.Exit(0);
    }

    private static object ProbePackage()
    {
        var locations = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var resources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var app = Assembly.Load("StreamStudio");
        foreach (var assembly in new[] { app, Assembly.Load("StreamlinkVlcStudio.Core"), Assembly.Load("StreamlinkVlcStudio.Infrastructure") })
        {
            locations.Add(assembly.GetName().Name!, assembly.Location);
            foreach (var name in assembly.GetManifestResourceNames())
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                resources.Add(assembly.GetName().Name + "/" + name, Convert.ToHexString(SHA256.HashData(stream)));
            }
        }

        using var markup = app.GetManifestResourceStream("StreamStudio.g.resources")
            ?? throw new InvalidDataException("The packaged WPF resources are missing.");
        using var reader = new ResourceReader(markup);
        var keys = reader.Cast<System.Collections.DictionaryEntry>().Select(entry => (string)entry.Key).ToArray();
        if (!keys.Contains("mainwindow.baml", StringComparer.Ordinal) ||
            !keys.Contains("assets/studio.ico", StringComparer.Ordinal) ||
            !resources.Keys.Any(name => name.EndsWith(".LocalWeb.Assets.app.js", StringComparison.Ordinal)) ||
            !resources.Keys.Any(name => name.StartsWith("StreamStudio/StreamStudio.ReleaseNotes.", StringComparison.Ordinal)))
            throw new InvalidDataException("The package is missing application resources.");

        return new
        {
            ManagedAssemblyLocations = locations, ResourceDigests = resources,
            WpfResourceCount = keys.Length, RenderedControls = PackageWpfProbe.Verify(app)
        };
    }

    private static object AwaitResult(object value)
    {
        ((Task)value).GetAwaiter().GetResult();
        return ReadProperty(value, "Result");
    }

    private static object ReadProperty(object value, string name) =>
        value.GetType().GetProperty(name)!.GetValue(value)
        ?? throw new InvalidOperationException($"The updater returned no {name}.");
}
