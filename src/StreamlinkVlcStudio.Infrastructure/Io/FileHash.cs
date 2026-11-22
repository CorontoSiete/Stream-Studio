using System.Security.Cryptography;

namespace StreamlinkVlcStudio.Infrastructure.Io;

internal static class FileHash
{
    internal static bool MatchesSha256(string path, string expectedHash)
    {
        try
        {
            // Cache repair can replace the path while this reader hashes the old file.
            var hash = GetSha256(path, FileShare.Read | FileShare.Delete);
            return string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static string GetSha256(string path, FileShare share = FileShare.Read)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
