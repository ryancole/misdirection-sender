using System.Collections.Concurrent;
using Misdirection.Client;

namespace Misdirection.Sender;

internal sealed record SendSettings
{
    /// <summary>Pause after each message.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Sent before the first message when set.</summary>
    public (ushort Width, ushort Height)? ScreenSize { get; init; }

    /// <summary>Stop and PANIC on the first NACK instead of carrying on.</summary>
    public bool StopOnNack { get; init; } = true;

    /// <summary>
    /// When set, a PING goes out after the last message and the send waits this long for the PONG.
    /// The firmware handles frames in order, so the PONG means every message was processed and any
    /// NACK they caused has already arrived.
    /// </summary>
    public TimeSpan? ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

internal enum SendStatus
{
    /// <summary>Every message was written.</summary>
    Completed,

    /// <summary>Stopped because the device NACKed; PANIC was sent.</summary>
    Nacked,

    /// <summary>Stopped by the caller's token; PANIC was sent.</summary>
    Cancelled,
}

/// <summary>
/// A NACK and how many messages had been written when it arrived. NACKs are asynchronous, so the
/// message that caused it is at or before that position.
/// </summary>
internal readonly record struct ReceivedNack(NackReason Reason, int SentBefore);

internal sealed record SendResult(SendStatus Status, int Sent, IReadOnlyList<ReceivedNack> Nacks, bool? Confirmed);

/// <summary>Writes a message sequence to a connected client, watching the back-channel for NACKs.</summary>
internal static class MessageSender
{
    public static async Task<SendResult> SendAsync(
        MisdirectionClient client,
        IReadOnlyList<Message> messages,
        SendSettings settings,
        Action<int, Message>? onSent = null,
        CancellationToken ct = default)
    {
        var sent = 0;
        var nacks = new ConcurrentQueue<ReceivedNack>();
        void OnNack(object? sender, NackMessage nack) => nacks.Enqueue(new(nack.Reason, Volatile.Read(ref sent)));

        client.NackReceived += OnNack;
        try
        {
            if (settings.ScreenSize is var (width, height))
                await client.ScreenSizeAsync(width, height, ct);

            foreach (var message in messages)
            {
                if (settings.StopOnNack && !nacks.IsEmpty)
                    return await AbortAsync(SendStatus.Nacked, confirmed: null);

                await client.SendAsync(message, ct);
                Interlocked.Increment(ref sent);
                onSent?.Invoke(sent, message);

                if (settings.Delay > TimeSpan.Zero)
                    await Task.Delay(settings.Delay, ct);
            }

            bool? confirmed = null;
            if (settings.ConfirmTimeout is { } timeout)
            {
                try
                {
                    await client.PingAsync(timeout, ct);
                    confirmed = true;
                }
                catch (TimeoutException)
                {
                    confirmed = false;
                }
            }

            if (settings.StopOnNack && !nacks.IsEmpty)
                return await AbortAsync(SendStatus.Nacked, confirmed);
            return new(SendStatus.Completed, sent, [.. nacks], confirmed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await AbortAsync(SendStatus.Cancelled, confirmed: null);
        }
        finally
        {
            client.NackReceived -= OnNack;
        }

        async Task<SendResult> AbortAsync(SendStatus status, bool? confirmed)
        {
            // Release anything the partial sequence left held. Best effort: the port may be the
            // reason we're stopping.
            try { await client.PanicAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            return new(status, sent, [.. nacks], confirmed);
        }
    }
}
