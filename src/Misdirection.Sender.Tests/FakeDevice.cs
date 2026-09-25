using System.IO.Pipelines;
using Misdirection.Client;

namespace Misdirection.Sender.Tests;

/// <summary>
/// In-memory stand-in for the firmware: records every message the host sends, answers PING with
/// PONG, and NACKs whatever <c>nackWhen</c> picks. Hand <see cref="Stream"/> to a client.
/// </summary>
public sealed class FakeDevice : IAsyncDisposable
{
    private readonly Pipe _hostToDevice = new();
    private readonly Pipe _deviceToHost = new();
    private readonly Func<Message, NackReason?> _nackWhen;
    private readonly List<Message> _received = [];
    private readonly Task _loop;

    public FakeDevice(Func<Message, NackReason?>? nackWhen = null, bool answerPing = true, byte version = Protocol.Version)
    {
        _nackWhen = nackWhen ?? (_ => null);
        AnswerPing = answerPing;
        Version = version;
        Stream = new DuplexStream(_deviceToHost.Reader.AsStream(), _hostToDevice.Writer.AsStream());
        _loop = Task.Run(RunAsync);
    }

    public Stream Stream { get; }

    public bool AnswerPing { get; set; }

    public byte Version { get; }

    /// <summary>Messages received so far, in order.</summary>
    public IReadOnlyList<Message> Received
    {
        get { lock (_received) return [.. _received]; }
    }

    /// <summary>Received messages other than PING, i.e. what the file put on the wire.</summary>
    public IReadOnlyList<Message> ReceivedExceptPings => [.. Received.Where(m => m is not PingMessage)];

    private async Task RunAsync()
    {
        var parser = new FrameParser();
        var input = _hostToDevice.Reader.AsStream();
        var buffer = new byte[256];
        int n;
        while ((n = await input.ReadAsync(buffer)) > 0)
        {
            foreach (var frame in parser.Feed(buffer.AsSpan(0, n)))
            {
                var message = Message.Decode(frame);
                lock (_received) _received.Add(message);

                if (message is PingMessage && AnswerPing)
                    await ReplyAsync(new PongMessage(Version));
                else if (_nackWhen(message) is { } reason)
                    await ReplyAsync(new NackMessage(reason));
            }
        }
    }

    private async Task ReplyAsync(Message message)
    {
        await _deviceToHost.Writer.WriteAsync(message.ToBytes());
        await _deviceToHost.Writer.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _hostToDevice.Writer.CompleteAsync();
        await _loop;
        await _deviceToHost.Writer.CompleteAsync();
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
