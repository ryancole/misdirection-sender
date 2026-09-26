using System.Runtime.CompilerServices;
using Misdirection.Client;

namespace Misdirection.Sender;

/// <summary>
/// <c>tail -f</c> for a protocol file: yields each wire message as it is appended, for as long as the
/// caller keeps asking. Parsing is as strict as <see cref="ProtocolFileReader"/>, except that the end of
/// the file is never an error: a header or frame cut short there is waited out, since the recorder
/// just hasn't written the rest yet. Delay records are consumed and not yielded; the file's timing is
/// ignored.
/// </summary>
/// <remarks>
/// If the file gets shorter than what has been read (a recorder re-created it), reading restarts from
/// its header, as <c>tail -f</c> does on truncation. A file truncated and rewritten past the read
/// position between two polls can't be told apart from one that grew, so that goes unnoticed.
/// </remarks>
internal sealed class FileFollower : IDisposable
{
    // How long to wait at the end of the file before looking again. Task.Delay rounds up to the
    // system timer tick (~15.6 ms on Windows), so this is really "as soon as the timer allows".
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);

    private readonly FileStream _stream;
    private readonly FrameParser _parser = new();
    private readonly byte[] _buffer = new byte[4096];
    private readonly byte[] _header = new byte[ProtocolFile.HeaderLength];
    private int _bufferLength;
    private int _bufferPos;
    private int _headerRead;
    private long _offset;
    private long _frameStart;
    private FrameDiscardReason? _discard;

    /// <summary>
    /// Opens <paramref name="path"/> read-only, sharing read and write so a recorder can keep appending
    /// (or re-create it). Nothing is read yet.
    /// </summary>
    public FileFollower(string path)
    {
        // Unbuffered: every read goes to the OS, so a read after hitting the end sees what was appended since.
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
        _parser.FrameDiscarded += r => _discard = r;
    }

    /// <summary>Called with each device-to-host message (PONG/NACK) passed over, and the offset of its frame.</summary>
    public Action<Message, long>? Skipped { get; init; }

    /// <summary>Called when the file got shorter than what had been read and reading restarted from the header.</summary>
    public Action? Truncated { get; init; }

    /// <summary>
    /// Reads every wire message already in the file, without waiting for more, and returns them. Call
    /// before <see cref="FollowAsync"/> to start following from the current end of the file.
    /// </summary>
    /// <exception cref="FollowException">The file is malformed or could not be read.</exception>
    public IReadOnlyList<Message> ReadExisting()
    {
        var messages = new List<Message>();
        while (TryReadNext(out var m))
            messages.Add(m);
        return messages;
    }

    /// <summary>Yields each wire message as it lands in the file. Never ends on its own; cancel to stop.</summary>
    /// <exception cref="FollowException">The file is malformed or could not be read.</exception>
    public async IAsyncEnumerable<Message> FollowAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            while (TryReadNext(out var m))
                yield return m;
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the next wire message; returns false at the current end of the file.</summary>
    private bool TryReadNext(out Message message)
    {
        try
        {
            return TryReadNextCore(out message);
        }
        catch (Exception ex) when (ex is ProtocolFileException or IOException or UnauthorizedAccessException)
        {
            throw new FollowException(ex.Message, ex);
        }
    }

    private bool TryReadNextCore(out Message message)
    {
        message = null!;
        while (true)
        {
            if (_bufferPos == _bufferLength)
            {
                _bufferLength = _stream.Read(_buffer, 0, _buffer.Length);
                _bufferPos = 0;
                if (_bufferLength == 0)
                {
                    if (_stream.Length >= _offset)
                        return false;
                    Restart();
                    continue;
                }
            }

            var b = _buffer[_bufferPos++];
            if (_headerRead < _header.Length)
            {
                _header[_headerRead++] = b;
                _offset++;
                if (_headerRead == _header.Length)
                    ProtocolFile.ReadHeader(new MemoryStream(_header, writable: false));
                continue;
            }

            if (_parser.IsIdle)
            {
                if (b != Protocol.StartOfFrame)
                    throw new ProtocolFileException($"Expected start of frame (0x{Protocol.StartOfFrame:X2}) at offset {_offset}, found 0x{b:X2}.");
                _frameStart = _offset;
            }
            _offset++;

            var complete = _parser.Push(b, out var frame);
            if (_discard is { } reason)
            {
                _discard = null;
                throw new ProtocolFileException($"{Describe(reason)} in the frame that starts at offset {_frameStart}.");
            }
            if (!complete)
                continue;

            if (!Message.TryDecode(frame!, out var decoded, out var error))
                throw new ProtocolFileException($"{error} (frame at offset {_frameStart})");
            if (decoded.IsFileOnly)
                continue;
            if (!decoded.IsHostToDevice)
            {
                Skipped?.Invoke(decoded, _frameStart);
                continue;
            }
            message = decoded;
            return true;
        }
    }

    private void Restart()
    {
        _stream.Position = 0;
        _offset = 0;
        _headerRead = 0;
        _bufferLength = _bufferPos = 0;
        _parser.Reset();
        Truncated?.Invoke();
    }

    private static string Describe(FrameDiscardReason reason) => reason switch
    {
        FrameDiscardReason.BadChecksum => "Bad checksum",
        FrameDiscardReason.UnknownType => "Unknown message type",
        FrameDiscardReason.BadLength => $"Payload length over {Protocol.MaxPayloadLength}",
        _ => reason.ToString(),
    };

    public void Dispose() => _stream.Dispose();
}

/// <summary>The followed file turned out malformed or could not be read; the message is shown to the user.</summary>
internal sealed class FollowException(string message, Exception inner) : Exception(message, inner);
