using System.Security.Cryptography;
using StreamlinkVlcStudio.Infrastructure.Io;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal sealed class BundledVlcBinary
{
    private readonly object extractionGate = new();
    private readonly string resourceName;
    private readonly string fileName;

    internal BundledVlcBinary(string resourceFolder, string fileName, string? expectedSha256 = null)
    {
        this.fileName = fileName;
        resourceName = $"StreamlinkVlcStudio.Infrastructure.Vlc.{resourceFolder}.{fileName}";
        using var resource = OpenResource();
        Sha256 = Convert.ToHexString(SHA256.HashData(resource));
        if (expectedSha256 is not null && !Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The bundled VLC binary {fileName} failed its SHA-256 check.");
    }

    internal string Sha256 { get; }

    internal string ExtractTo(string directory)
    {
        lock (extractionGate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            if (FileHash.MatchesSha256(path, Sha256)) return path;

            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var resource = OpenResource())
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    resource.CopyTo(output);

                try { File.Move(temporary, path, overwrite: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException &&
                    FileHash.MatchesSha256(path, Sha256))
                {
                    // Another process already extracted the same content-addressed binary.
                }

                if (!FileHash.MatchesSha256(path, Sha256))
                    throw new IOException($"The bundled VLC binary {fileName} could not be extracted.");
                return path;
            }
            finally
            {
                AtomicFile.TryDeleteTemporaryFile(temporary);
            }
        }
    }

    private Stream OpenResource() => typeof(BundledVlcBinary).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new FileNotFoundException($"The bundled VLC binary {fileName} is missing.");
}
