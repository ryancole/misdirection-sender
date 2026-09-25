using Misdirection.Client;

namespace Misdirection.Sender.Tests;

public class MessageSenderTests
{
    private static readonly Message[] Drag =
    [
        new KeyDownMessage(HidUsage.LeftShift),
        new MouseMoveMessage(100, 100),
        new MouseButtonsMessage(MouseButtons.Left),
        new MouseMoveMessage(500, 400),
        new MouseButtonsMessage(MouseButtons.None),
        new KeyUpMessage(HidUsage.LeftShift),
    ];

    [Fact]
    public async Task SendsEveryMessageInOrderThenConfirmsWithPing()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var result = await MessageSender.SendAsync(client, Drag, new SendSettings());

        Assert.Equal(SendStatus.Completed, result.Status);
        Assert.Equal(Drag.Length, result.Sent);
        Assert.True(result.Confirmed);
        Assert.Empty(result.Nacks);
        Assert.Equal([.. Drag, new PingMessage()], device.Received);
    }

    [Fact]
    public async Task ScreenSizeGoesFirst()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        await MessageSender.SendAsync(client, Drag, new SendSettings { ScreenSize = (2560, 1440) });

        Assert.Equal(new ScreenSizeMessage(2560, 1440), device.Received[0]);
        Assert.Equal(Drag, device.ReceivedExceptPings.Skip(1));
    }

    [Fact]
    public async Task StopsAndPanicsOnNack()
    {
        // NACK the first mouse move; with a delay between messages the NACK lands before the
        // sequence finishes, so the sender stops short.
        await using var device = new FakeDevice(m => m is MouseMoveMessage ? NackReason.Disarmed : null);
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var result = await MessageSender.SendAsync(client, Drag, new SendSettings { Delay = TimeSpan.FromMilliseconds(100) });

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

        var result = await MessageSender.SendAsync(client, Drag, new SendSettings());

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
            client, Drag, new SendSettings { StopOnNack = false, Delay = TimeSpan.FromMilliseconds(20) });

        Assert.Equal(SendStatus.Completed, result.Status);
        Assert.Equal(Drag, device.ReceivedExceptPings);
        Assert.Equal(2, result.Nacks.Count);
    }

    [Fact]
    public async Task CancellationDuringDelayStopsAndPanics()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);
        using var cts = new CancellationTokenSource();

        var result = await MessageSender.SendAsync(
            client, Drag, new SendSettings { Delay = TimeSpan.FromSeconds(10) },
            onSent: (n, _) => { if (n == 1) cts.CancelAfter(50); },
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
            client, Drag, new SendSettings { ConfirmTimeout = TimeSpan.FromMilliseconds(100) });

        Assert.Equal(SendStatus.Completed, result.Status);
        Assert.False(result.Confirmed);
    }

    [Fact]
    public async Task NoConfirmTimeoutSkipsPing()
    {
        await using var device = new FakeDevice();
        await using var client = new MisdirectionClient(device.Stream, leaveOpen: true);

        var result = await MessageSender.SendAsync(client, Drag, new SendSettings { ConfirmTimeout = null });

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
