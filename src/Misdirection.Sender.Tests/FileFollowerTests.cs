using Misdirection.Client;

namespace Misdirection.Sender.Tests;

public sealed class FileFollowerTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly string _dir = Directory.CreateTempSubdirectory("misdirection-follower-tests").FullName;

    private static readonly Message[] Tap = [new KeyDownMessage(HidUsage.A), new KeyUpMessage(HidUsage.A)];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.msdr");

    /// <summary>Appends raw bytes the way a second process would, without holding the file open.</summary>
    private static void AppendBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }

    private static byte[] Encode(Message message)
    {
        var buffer = new byte[Protocol.FrameOverhead + Protocol.MaxPayloadLength];
        return buffer[..message.ToFrame().Encode(buffer)];
    }

    private static async Task<Message> NextAsync(IAsyncEnumerator<Message> e)
    {
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Patience));
        return e.Current;
    }

    [Fact]
    public void ReadExistingReturnsWireMessagesOnly()
    {
        var path = NewPath();
        using (var writer = ProtocolFileWriter.Create(path))
        {
            writer.Write(Tap[0]);
            writer.WriteDelay(TimeSpan.FromMilliseconds(5));
            writer.Write(new PongMessage(1));
            writer.Write(Tap[1]);
        }
        var skipped = new List<long>();
        using var follower = new FileFollower(path) { Skipped = (_, offset) => skipped.Add(offset) };

        Assert.Equal(Tap, follower.ReadExisting());
        Assert.Single(skipped);
    }

    [Fact]
    public async Task YieldsWhatARecorderAppends()
    {
        var path = NewPath();
        using var writer = ProtocolFileWriter.Create(path);
        writer.Write(Tap[0]);
        writer.Flush();
        using var follower = new FileFollower(path);
        Assert.Equal([Tap[0]], follower.ReadExisting());

        await using var e = follower.FollowAsync().GetAsyncEnumerator();
        var next = NextAsync(e);
        writer.WriteDelay(TimeSpan.FromSeconds(30)); // ignored: it arrives right away
        writer.Write(Tap[1]);
        writer.Flush();

        Assert.Equal(Tap[1], await next);
    }

    [Fact]
    public async Task WaitsOutAHeaderAndFrameCutShort()
    {
        var path = NewPath();
        var bytes = new MemoryStream();
        ProtocolFile.Write(bytes, [Tap[0]]);
        var all = bytes.ToArray();
        AppendBytes(path, all.AsSpan(0, 3)); // half a header
        using var follower = new FileFollower(path);
        Assert.Empty(follower.ReadExisting());

        await using var e = follower.FollowAsync().GetAsyncEnumerator();
        var next = NextAsync(e);
        AppendBytes(path, all.AsSpan(3, all.Length - 4)); // the rest of the header, most of the frame
        await Task.Delay(50);
        Assert.False(next.IsCompleted);
        AppendBytes(path, all.AsSpan(all.Length - 1));

        Assert.Equal(Tap[0], await next);
    }

    [Fact]
    public async Task RestartsWhenTheFileIsRecreated()
    {
        var path = NewPath();
        ProtocolFile.Write(path, [.. Tap, .. Tap]);
        var truncated = 0;
        using var follower = new FileFollower(path) { Truncated = () => truncated++ };
        Assert.Equal(4, follower.ReadExisting().Count);

        await using var e = follower.FollowAsync().GetAsyncEnumerator();
        var next = NextAsync(e);
        var click = new MouseButtonsMessage(MouseButtons.Left);
        ProtocolFile.Write(path, [click]);

        Assert.Equal(click, await next);
        Assert.Equal(1, truncated);
    }

    [Fact]
    public async Task MalformedAppendThrows()
    {
        var path = NewPath();
        ProtocolFile.Write(path, Tap);
        using var follower = new FileFollower(path);
        follower.ReadExisting();

        await using var e = follower.FollowAsync().GetAsyncEnumerator();
        AppendBytes(path, [0x00]);

        var ex = await Assert.ThrowsAsync<FollowException>(() => e.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Contains("Expected start of frame", ex.Message);
    }

    [Fact]
    public void BadHeaderThrows()
    {
        var path = NewPath();
        AppendBytes(path, "NOPE\x01\x01"u8);
        using var follower = new FileFollower(path);

        Assert.Throws<FollowException>(() => follower.ReadExisting());
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        var path = NewPath();
        ProtocolFile.Write(path, Tap);
        using var follower = new FileFollower(path);
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in follower.FollowAsync(cts.Token)) { }
        });
    }
}
