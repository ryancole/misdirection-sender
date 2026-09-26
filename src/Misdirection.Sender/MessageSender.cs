using System.Collections.Concurrent;
using Misdirection.Client;

namespace Misdirection.Sender;

internal sealed record SendSettings
{
    /// <summary>Playback rate for the file's timing: 2 plays twice as fast, 0.5 half as fast.</summary>
    public double Speed { get; init; } = 1;

    /// <summary>Send without waiting for the file's timing; only <see cref="MinimumGap"/> paces.</summary>
    public bool IgnoreTiming { get; init; }

    /// <summary>Least time between the starts of consecutive messages, applied on top of the file's timing.</summary>
    public TimeSpan MinimumGap { get; init; }

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

    /// <summary>When the message recorded at <paramref name="at"/> is due, before the minimum gap is applied.</summary>
    public TimeSpan Scheduled(TimeSpan at) => IgnoreTiming ? TimeSpan.Zero : at / Speed;
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

/// <summary>
/// Plays a timed message sequence to a connected client, watching the back-channel for NACKs. Each
/// message's <c>At</c> is its offset from the start of playback, as <see cref="ProtocolFile.ReadTimed(string)"/>
/// yields it.
/// </summary>
internal static class MessageSender
{
    /// <param name="onSent">Called after each message with its 1-based position and when it went out.</param>
    public static Task<SendResult> SendAsync(
        MisdirectionClient client,
        IReadOnlyList<(TimeSpan At, Message Message)> messages,
        SendSettings settings,
        Action<int, TimeSpan, Message>? onSent = null,
        CancellationToken ct = default) =>
        SendAsync(client, messages.ToAsyncEnumerable(), settings, onSent, ct);

    /// <summary>
    /// Sends messages as <paramref name="messages"/> produces them, so the sequence can be open-ended
    /// (see <see cref="FileFollower"/>). If it throws, PANIC is sent and the exception is rethrown.
    /// </summary>
    /// <param name="onSent">Called after each message with its 1-based position and when it went out.</param>
    public static async Task<SendResult> SendAsync(
        MisdirectionClient client,
        IAsyncEnumerable<(TimeSpan At, Message Message)> messages,
        SendSettings settings,
        Action<int, TimeSpan, Message>? onSent = null,
        CancellationToken ct = default)
    {
        var sent = 0;
        var nacks = new ConcurrentQueue<ReceivedNack>();

        // With StopOnNack, the first NACK cancels this so a wait, for a message's time or for the next
        // message to exist at all, ends as soon as the device objects. Only waits take it: cancelling
        // a write could tear a frame on the wire.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void OnNack(object? sender, NackMessage nack)
        {
            nacks.Enqueue(new(nack.Reason, Volatile.Read(ref sent)));
            if (!settings.StopOnNack) return;
            // Async, so the waiter's continuation doesn't run on the client's reader thread.
            try { _ = stop.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        client.NackReceived += OnNack;
        try
        {
            if (settings.ScreenSize is var (width, height))
                await client.ScreenSizeAsync(width, height, ct);

            var clock = new PlaybackClock();
            TimeSpan? previous = null;
            await foreach (var (at, message) in messages.WithCancellation(stop.Token))
            {
                var due = settings.Scheduled(at);
                if (previous is { } p && p + settings.MinimumGap > due)
                    due = p + settings.MinimumGap;
                await clock.WaitUntilAsync(due, stop.Token);

                if (settings.StopOnNack && !nacks.IsEmpty)
                    return await AbortAsync(SendStatus.Nacked, confirmed: null);

                var sentAt = clock.Elapsed;
                await client.SendAsync(message, ct);
                Interlocked.Increment(ref sent);
                previous = sentAt;
                onSent?.Invoke(sent, sentAt, message);
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
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return await AbortAsync(SendStatus.Nacked, confirmed: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await PanicAsync();
            throw;
        }
        finally
        {
            client.NackReceived -= OnNack;
        }

        async Task<SendResult> AbortAsync(SendStatus status, bool? confirmed)
        {
            await PanicAsync();
            return new(status, sent, [.. nacks], confirmed);
        }

        // Release anything the partial sequence left held. Best effort: the port may be the reason
        // we're stopping.
        async Task PanicAsync()
        {
            try { await client.PanicAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }
}
