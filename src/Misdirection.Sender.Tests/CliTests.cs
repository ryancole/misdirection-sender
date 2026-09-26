using Misdirection.Client;

namespace Misdirection.Sender.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("misdirection-sender-tests").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private static readonly Message[] Tap = [new KeyDownMessage(HidUsage.A), new KeyUpMessage(HidUsage.A)];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(IEnumerable<Message> messages)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.msdr");
        ProtocolFile.Write(path, messages);
        return path;
    }

    private Cli CliFor(FakeDevice device, List<(string, int)>? opened = null) =>
        new(_out, _err, (port, baud) =>
        {
            opened?.Add((port, baud));
            return new MisdirectionClient(device.Stream, leaveOpen: true);
        });

    [Fact]
    public async Task SendsFileToPort()
    {
        await using var device = new FakeDevice();
        var opened = new List<(string, int)>();
        var file = WriteFile(Tap);

        var code = await CliFor(device, opened).RunAsync([file, "-p", "COM9", "-b", "9600"]);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal([("COM9", 9600)], opened);
        Assert.Equal([new PingMessage(), .. Tap, new PingMessage()], device.Received);
        Assert.Contains("Sent 2 message(s)", _out.ToString());
    }

    [Fact]
    public async Task NoPingSendsOnlyTheFile()
    {
        await using var device = new FakeDevice();
        var file = WriteFile(Tap);

        var code = await CliFor(device).RunAsync([file, "-p", "COM9", "--no-ping"]);

        Assert.Equal(ExitCodes.Ok, code);
        await MessageSenderTests.WaitForAsync(() => device.Received.Count == Tap.Length);
        Assert.Equal(Tap, device.Received);
    }

    [Fact]
    public async Task DryRunListsMessagesWithoutOpeningPort()
    {
        var file = WriteFile(Tap);
        var cli = new Cli(_out, _err, (_, _) => throw new InvalidOperationException("port opened"));

        var code = await cli.RunAsync([file, "--dry-run"]);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("2 message(s)", _out.ToString());
        Assert.Contains("KeyDownMessage", _out.ToString());
    }

    [Fact]
    public async Task FileHeldOpenByARecorderCanStillBeRead()
    {
        var file = WriteFile(Tap);
        // A recorder appends for the whole session and keeps the file open, the way
        // ProtocolFileWriter.Append does; playing what it has written so far must not need it closed.
        using var recorder = ProtocolFileWriter.Append(file);
        var cli = new Cli(_out, _err, (_, _) => throw new InvalidOperationException("port opened"));

        var code = await cli.RunAsync([file, "--dry-run"]);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("2 message(s)", _out.ToString());
    }

    [Fact]
    public async Task MalformedFileSendsNothing()
    {
        await using var device = new FakeDevice();
        var file = WriteFile(Tap);
        File.AppendAllBytes(file, [0xAB, 0x01]); // truncated frame

        var code = await CliFor(device).RunAsync([file, "-p", "COM9"]);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("ends inside the frame", _err.ToString());
        Assert.Empty(device.Received);
    }

    [Fact]
    public async Task FolderSendsItsMostRecentlyWrittenFile()
    {
        await using var device = new FakeDevice();
        var older = WriteFile([new KeyDownMessage(HidUsage.Enter)]);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        var newer = WriteFile(Tap);
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a recording");

        var code = await CliFor(device).RunAsync([_dir, "-p", "COM9"]);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(Tap, device.ReceivedExceptPings);
        Assert.Contains($"Using {newer}", _out.ToString());
    }

    [Fact]
    public async Task FolderWithNoRecordingsIsAnError()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a recording");

        var code = await new Cli(_out, _err).RunAsync([_dir, "--dry-run"]);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("no .msdr files", _err.ToString());
    }

    [Fact]
    public async Task FollowWithAFolderFollowsItsLatestFile()
    {
        await using var device = new FakeDevice();
        var older = WriteFile([]);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        var newer = WriteFile([]);
        var (run, ctrlC) = Start(CliFor(device), _dir, "-p", "COM9", "--follow");
        using var _ = ctrlC;

        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 1);
        Append(older, new KeyDownMessage(HidUsage.Enter));
        Append(newer, Tap[0]);
        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 2);
        ctrlC.Cancel();

        Assert.Equal(ExitCodes.Cancelled, await run);
        Assert.Equal([new PingMessage(), Tap[0], new PanicMessage()], device.Received);
    }

    [Fact]
    public async Task MissingFileIsAnError()
    {
        var code = await new Cli(_out, _err).RunAsync([Path.Combine(_dir, "nope.msdr"), "--dry-run"]);
        Assert.Equal(ExitCodes.Error, code);
    }

    [Fact]
    public async Task SilentDeviceFailsHandshake()
    {
        await using var device = new FakeDevice(answerPing: false);
        var file = WriteFile(Tap);

        var code = await CliFor(device).RunAsync([file, "-p", "COM9"]);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("no reply to PING", _err.ToString());
        Assert.Equal([new PingMessage()], device.Received);
    }

    [Fact]
    public async Task VersionMismatchFailsHandshake()
    {
        await using var device = new FakeDevice(version: 99);
        var file = WriteFile(Tap);

        var code = await CliFor(device).RunAsync([file, "-p", "COM9"]);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("protocol v99", _err.ToString());
    }

    [Fact]
    public async Task NackExitsWithNackCode()
    {
        await using var device = new FakeDevice(m => m is KeyUpMessage ? NackReason.Disarmed : null);
        var file = WriteFile(Tap);

        var code = await CliFor(device).RunAsync([file, "-p", "COM9"]);

        Assert.Equal(ExitCodes.Nacked, code);
        Assert.Contains("NACK Disarmed", _err.ToString());
    }

    [Fact]
    public async Task DeviceToHostMessagesAreSkipped()
    {
        await using var device = new FakeDevice();
        var file = WriteFile([Tap[0], new PongMessage(1), Tap[1]]);

        var code = await CliFor(device).RunAsync([file, "-p", "COM9"]);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("skipping 1", _err.ToString());
        Assert.Equal(Tap, device.ReceivedExceptPings);
    }

    [Fact]
    public async Task ByDefaultEachButtonsMessageIsPrefixedWithTheLastMove()
    {
        await using var device = new FakeDevice();
        var move = new MouseMoveMessage(100, 100);
        var press = new MouseButtonsMessage(MouseButtons.Left);
        var release = new MouseButtonsMessage(MouseButtons.None);
        var file = WriteFile([move, Tap[0], press, release]);

        var code = await CliFor(device).RunAsync([file, "-p", "COM9"]);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal([move, Tap[0], move, press, move, release], device.ReceivedExceptPings);
        Assert.Contains("6 message(s)", _out.ToString());
    }

    [Fact]
    public async Task NoMoveBeforeClickSendsTheFileAsIs()
    {
        await using var device = new FakeDevice();
        Message[] messages = [new MouseMoveMessage(100, 100), Tap[0], new MouseButtonsMessage(MouseButtons.Left)];
        var file = WriteFile(messages);

        var code = await CliFor(device).RunAsync([file, "-p", "COM9", "--no-move-before-click"]);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(messages, device.ReceivedExceptPings);
    }

    private string WriteTimedFile()
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.msdr");
        using var writer = ProtocolFileWriter.Create(path);
        writer.Write(Tap[0]);
        writer.WriteDelay(TimeSpan.FromMilliseconds(150));
        writer.Write(Tap[1]);
        return path;
    }

    [Fact]
    public async Task DelaysPaceTheSendAndNeverReachTheDevice()
    {
        await using var device = new FakeDevice();
        var file = WriteTimedFile();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var code = await CliFor(device).RunAsync([file, "-p", "COM9"]);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(150));
        Assert.Equal([new PingMessage(), .. Tap, new PingMessage()], device.Received);
        Assert.Contains("2 message(s) over 0.150s", _out.ToString());
    }

    [Fact]
    public async Task DryRunShowsScheduleAtSpeed()
    {
        var file = WriteTimedFile();

        var code = await new Cli(_out, _err).RunAsync([file, "--dry-run", "--speed", "2"]);

        Assert.Equal(ExitCodes.Ok, code);
        var output = _out.ToString();
        Assert.Contains("over 0.075s at 2x (recorded 0.150s)", output);
        Assert.Contains("0.075s  KeyUpMessage", output);
        Assert.DoesNotContain("DelayMessage", output);
    }

    /// <summary>
    /// Runs the CLI in the background with <paramref name="args"/>. The returned token source is Ctrl+C.
    /// </summary>
    private (Task<int> Run, CancellationTokenSource CtrlC) Start(Cli cli, params string[] args)
    {
        var cts = new CancellationTokenSource();
        return (Task.Run(() => cli.RunAsync(args, cts.Token)), cts);
    }

    private static void Append(string file, params Message[] messages)
    {
        using var writer = ProtocolFileWriter.Append(file);
        writer.Write(messages);
    }

    [Fact]
    public async Task FollowSendsOnlyWhatIsAppendedUntilCtrlC()
    {
        await using var device = new FakeDevice();
        var file = WriteFile(Tap);
        var (run, ctrlC) = Start(CliFor(device), file, "-p", "COM9", "--follow");
        using var _ = ctrlC;

        // The handshake PING goes out after the existing content is read, so what's appended from
        // here on is new.
        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 1);
        var enter = new KeyDownMessage(HidUsage.Enter);
        Append(file, enter);
        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 2);
        ctrlC.Cancel();

        Assert.Equal(ExitCodes.Cancelled, await run);
        Assert.Equal([new PingMessage(), enter, new PanicMessage()], device.Received);
        Assert.Contains("skipped the 2 message(s)", _out.ToString());
    }

    [Fact]
    public async Task FollowFromStartSendsTheExistingContentFirst()
    {
        await using var device = new FakeDevice();
        var file = WriteFile(Tap);
        var (run, ctrlC) = Start(CliFor(device), file, "-p", "COM9", "--follow", "--from-start", "--no-ping");
        using var _ = ctrlC;

        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 2);
        Append(file, Tap);
        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 4);
        ctrlC.Cancel();

        Assert.Equal(ExitCodes.Cancelled, await run);
        Assert.Equal([.. Tap, .. Tap], device.ReceivedExceptPings.SkipLast(1));
    }

    [Fact]
    public async Task FollowRemembersTheLastMoveInSkippedContent()
    {
        await using var device = new FakeDevice();
        var move = new MouseMoveMessage(100, 100);
        var file = WriteFile([move]);
        var (run, ctrlC) = Start(CliFor(device), file, "-p", "COM9", "--follow");
        using var _ = ctrlC;

        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 1);
        var press = new MouseButtonsMessage(MouseButtons.Left);
        Append(file, press);
        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 3);
        ctrlC.Cancel();

        await run;
        Assert.Equal([new PingMessage(), move, press, new PanicMessage()], device.Received);
    }

    [Fact]
    public async Task FollowStopsOnNackWithoutAnotherMessage()
    {
        await using var device = new FakeDevice(m => m is KeyUpMessage ? NackReason.Disarmed : null);
        var file = WriteFile([]);
        var (run, ctrlC) = Start(CliFor(device), file, "-p", "COM9", "--follow");
        using var _ = ctrlC;

        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 1);
        Append(file, Tap);

        Assert.Equal(ExitCodes.Nacked, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("NACK Disarmed", _err.ToString());
        Assert.IsType<PanicMessage>(device.Received[^1]);
    }

    [Fact]
    public async Task FollowReportsAMalformedAppendAndPanics()
    {
        await using var device = new FakeDevice();
        var file = WriteFile(Tap);
        var (run, ctrlC) = Start(CliFor(device), file, "-p", "COM9", "--follow");
        using var _ = ctrlC;

        await MessageSenderTests.WaitForAsync(() => device.Received.Count == 1);
        File.AppendAllBytes(file, [0x00]);

        Assert.Equal(ExitCodes.Error, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Expected start of frame", _err.ToString());
        await MessageSenderTests.WaitForAsync(() => device.Received.LastOrDefault() is PanicMessage);
    }

    [Fact]
    public async Task FollowWithAMalformedFileSendsNothing()
    {
        await using var device = new FakeDevice();
        var file = WriteFile(Tap);
        File.AppendAllBytes(file, [0x00]);

        var code = await CliFor(device).RunAsync([file, "-p", "COM9", "--follow"]);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Empty(device.Received);
    }

    [Fact]
    public async Task FollowDryRunListsAppendedMessages()
    {
        var file = WriteFile(Tap);
        var cli = new Cli(_out, _err, (_, _) => throw new InvalidOperationException("port opened"));
        var (run, ctrlC) = Start(cli, file, "--follow", "--dry-run");
        using var _ = ctrlC;

        await MessageSenderTests.WaitForAsync(() => _out.ToString().Contains("Following"));
        Append(file, new KeyDownMessage(HidUsage.Enter));
        await MessageSenderTests.WaitForAsync(() => _out.ToString().Contains("KeyDownMessage"));
        ctrlC.Cancel();

        Assert.Equal(ExitCodes.Cancelled, await run);
        Assert.Contains("Stopped after 1 message(s)", _err.ToString());
    }

    [Fact]
    public async Task BadArgumentsExitWithUsageCode()
    {
        Assert.Equal(ExitCodes.Usage, await new Cli(_out, _err).RunAsync(["--frob"]));
        Assert.Contains("--help", _err.ToString());
    }
}
