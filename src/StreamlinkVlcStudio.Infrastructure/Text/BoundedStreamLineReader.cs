using System.Text;

namespace StreamlinkVlcStudio.Infrastructure.Text;

/// <summary>Reads and drains byte-delimited lines while retaining at most a fixed number of bytes.</summary>
internal sealed class BoundedStreamLineReader : IDisposable
{
    private readonly BufferedByteReader reader;
    private readonly Encoding encoding;
    private readonly byte[] lineBuffer;

    internal BoundedStreamLineReader(
        Stream stream,
        Encoding encoding,
        int maximumLineBytes,
        bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumLineBytes, 0);
        reader = new BufferedByteReader(stream, leaveOpen);
        lineBuffer = new byte[maximumLineBytes];
    }

    internal async Task<BoundedTextLine?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        var totalLength = 0;
        var lastByte = -1;
        while (true)
        {
            var next = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
            {
                if (totalLength == 0)
                {
                    return null;
                }

                break;
            }

            if (next == '\n')
            {
                break;
            }

            if (totalLength < lineBuffer.Length) lineBuffer[totalLength] = (byte)next;
            if (totalLength < int.MaxValue) totalLength++;
            lastByte = next;
        }

        // Exclude the trailing CR even when it falls outside the retained buffer.
        if (lastByte == '\r') totalLength--;
        if (totalLength <= lineBuffer.Length)
            return new BoundedTextLine(encoding.GetString(lineBuffer, 0, totalLength), false);

        // An otherwise valid multibyte character can straddle the retained prefix.
        // Decode without flushing so the incomplete trailing bytes are ignored.
        var characters = new char[encoding.GetMaxCharCount(lineBuffer.Length)];
        var count = encoding.GetDecoder().GetChars(lineBuffer, characters, flush: false);
        return new BoundedTextLine(new string(characters, 0, count), true);
    }

    public void Dispose() => reader.Dispose();
}

internal readonly record struct BoundedTextLine(string Text, bool WasTruncated);
