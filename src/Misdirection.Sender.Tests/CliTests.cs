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

    [Fact]
    public async Task BadArgumentsExitWithUsageCode()
    {
        Assert.Equal(ExitCodes.Usage, await new Cli(_out, _err).RunAsync(["--frob"]));
        Assert.Contains("--help", _err.ToString());
    }
}
