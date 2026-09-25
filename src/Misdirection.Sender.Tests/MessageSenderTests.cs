using Misdirection.Client;

namespace Misdirection.Sender.Tests;

public class MessageSenderTests
{
    // Generous so a loaded machine doesn't fail the suite; the spin phase is normally well under 1 ms late.
    private static readonly TimeSpan Lateness = TimeSpan.FromMilliseconds(30);

    private static readonly Message[] Drag =
    [
        new KeyDownMessage(HidUsage.LeftShift),
        new MouseMoveMessage(100, 100),
        new MouseButtonsMessage(MouseButtons.Left),
        new MouseMoveMessage(500, 400),
        new MouseButtonsMessage(MouseButtons.None),
        new KeyUpMessage(HidUsage.LeftShift),
    ];

    private static (TimeSpan At, Message Message)[] Untimed(IEnumerable<Message> messages) =>
        [.. messages.Select(m => (TimeSpan.Zero, m))];

    private static (TimeSpan At, Message Message)[] Timed(params int[] atMilliseconds) =>
        [.. atMilliseconds.Select((ms, i) => (TimeSpan.FromMilliseconds(ms), Drag[i]))];

    /// <summary>Sends and returns when each message went out, by the sender's playback clock.</summary>
    private static async Task<(SendResult Result, List<TimeSpan> SentAt)> SendTimedAsync(
        FakeDevice device, IReadOnlyList<(TimeSpan, Message)> messages, SendSettings settings)
    {
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);
        var sentAt = new List<TimeSpan>();
        var result = await MessageSender.SendAsync(client, messages, settings, (_, at, _) => sentAt.Add(at));
        return (result, sentAt);
    }

    private static void AssertOnTime(TimeSpan expected, TimeSpan actual)
    {
        Assert.True(actual >= expected, $"sent at {actual.TotalMilliseconds} ms, before {expected.TotalMilliseconds} ms");
        Assert.True(actual < expected + Lateness, $"sent at {actual.TotalMilliseconds} ms, due {expected.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task SendsEveryMessageInOrderThenConfirmsWithPing()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var result = await MessageSender.SendAsync(client, Untimed(Drag), new SendSettings());

        Assert.Equal(SendStatus.Completed, result.Status);
        Assert.Equal(Drag.Length, result.Sent);
        Assert.True(result.Confirmed);
        Assert.Empty(result.Nacks);
        Assert.Equal([.. Drag, new PingMessage()], device.Received);
    }

    [Fact]
    public async Task FollowsRecordedTiming()
    {
        await using var device = new FakeDevice();
        var file = Timed(0, 40, 45, 120, 200);

        var (_, sentAt) = await SendTimedAsync(device, file, new SendSettings());

        for (var i = 0; i < file.Length; i++)
            AssertOnTime(file[i].At, sentAt[i]);
    }

    [Fact]
    public async Task SpeedScalesTiming()
    {
        await using var device = new FakeDevice();
        var file = Timed(0, 100, 200);

        var (_, sentAt) = await SendTimedAsync(device, file, new SendSettings { Speed = 2 });

        AssertOnTime(TimeSpan.FromMilliseconds(50), sentAt[1]);
        AssertOnTime(TimeSpan.FromMilliseconds(100), sentAt[2]);
    }

    [Fact]
    public async Task IgnoreTimingSendsWithoutWaiting()
    {
        await using var device = new FakeDevice();

        var (result, sentAt) = await SendTimedAsync(device, Timed(0, 10_000, 20_000), new SendSettings { IgnoreTiming = true });

        Assert.Equal(SendStatus.Completed, result.Status);
        Assert.True(sentAt[^1] < Lateness);
    }

    [Fact]
    public async Task MinimumGapAppliesOnTopOfTiming()
    {
        await using var device = new FakeDevice();

        // Two messages due together, then one well after the gap has passed.
        var (_, sentAt) = await SendTimedAsync(
            device, Timed(0, 0, 200), new SendSettings { MinimumGap = TimeSpan.FromMilliseconds(50) });

        Assert.True(sentAt[1] - sentAt[0] >= TimeSpan.FromMilliseconds(50));
        AssertOnTime(TimeSpan.FromMilliseconds(200), sentAt[2]);
    }

    [Fact]
    public void ScheduleIsAtOverSpeedUnlessTimingIgnored()
    {
        var at = TimeSpan.FromSeconds(3);
        Assert.Equal(at, new SendSettings().Scheduled(at));
        Assert.Equal(TimeSpan.FromSeconds(1.5), new SendSettings { Speed = 2 }.Scheduled(at));
        Assert.Equal(TimeSpan.Zero, new SendSettings { IgnoreTiming = true }.Scheduled(at));
    }

    [Fact]
    public async Task ScreenSizeGoesFirst()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        await MessageSender.SendAsync(client, Untimed(Drag), new SendSettings { ScreenSize = (2560, 1440) });

        Assert.Equal(new ScreenSizeMessage(2560, 1440), device.Received[0]);
        Assert.Equal(Drag, device.ReceivedExceptPings.Skip(1));
    }

    [Fact]
    public async Task StopsAndPanicsOnNack()
    {
        // NACK the first mouse move; with gaps between messages the NACK lands before the sequence
        // finishes, so the sender stops short.
        await using var device = new FakeDevice(m => m is MouseMoveMessage ? NackReason.Disarmed : null);
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var result = await MessageSender.SendAsync(
            client, Untimed(Drag), new SendSettings { MinimumGap = TimeSpan.FromMilliseconds(100) });

        Assert.Equal(SendStatus.Nacked, result.Status);
        Assert.True(result.Sent < Drag.Length);
        Assert.Equal(NackReason.Disarmed, result.Nacks[0].Reason);
        Assert.True(result.Nacks[0].SentBefore >= 2);
        await WaitForAsync(() => device.Received.LastOrDefault() is PanicMessage);
    }

    [Fact]
    public async Task NackOnLastMessageIsCaughtByConfirmPing()
    {
        await using var device = new FakeDevice(m => m is KeyUpMessage ? NackReason.KeyRolloverFull : null);
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var result = await MessageSender.SendAsync(client, Untimed(Drag), new SendSettings());

        Assert.Equal(SendStatus.Nacked, result.Status);
        Assert.Equal(Drag.Length, result.Sent);
        Assert.Equal([new ReceivedNack(NackReason.KeyRolloverFull, Drag.Length)], result.Nacks);
    }

    [Fact]
    public async Task ContinueOnNackSendsEverything()
    {
        await using var device = new FakeDevice(m => m is MouseMoveMessage ? NackReason.Disarmed : null);
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var result = await MessageSender.SendAsync(
            client, Untimed(Drag), new SendSettings { StopOnNack = false, MinimumGap = TimeSpan.FromMilliseconds(20) });

        Assert.Equal(SendStatus.Completed, result.Status);
        Assert.Equal(Drag, device.ReceivedExceptPings);
        Assert.Equal(2, result.Nacks.Count);
    }

    [Fact]
    public async Task CancellationDuringWaitStopsAndPanics()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);
        using var cts = new CancellationTokenSource();

        var result = await MessageSender.SendAsync(
            client, Timed(0, 10_000), new SendSettings(),
            onSent: (n, _, _) => { if (n == 1) cts.CancelAfter(50); },
            ct: cts.Token);

        Assert.Equal(SendStatus.Cancelled, result.Status);
        Assert.Equal(1, result.Sent);
        await WaitForAsync(() => device.Received.Count == 2);
        Assert.Equal([Drag[0], new PanicMessage()], device.Received);
    }

    [Fact]
    public async Task SilentDeviceLeavesDeliveryUnconfirmed()
    {
        await using var device = new FakeDevice(answerPing: false);
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var result = await MessageSender.SendAsync(
            client, Untimed(Drag), new SendSettings { ConfirmTimeout = TimeSpan.FromMilliseconds(100) });

        Assert.Equal(SendStatus.Completed, result.Status);
        Assert.False(result.Confirmed);
    }

    [Fact]
    public async Task NoConfirmTimeoutSkipsPing()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var result = await MessageSender.SendAsync(client, Untimed(Drag), new SendSettings { ConfirmTimeout = null });

        Assert.Null(result.Confirmed);
        await WaitForAsync(() => device.Received.Count == Drag.Length);
        Assert.Equal(Drag, device.Received);
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Condition not met within 5s.");
            await Task.Delay(10);
        }
    }
}
