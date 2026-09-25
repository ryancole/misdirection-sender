using System.Diagnostics;

namespace Misdirection.Sender;

/// <summary>
/// A playback timeline that starts when the clock is created. Every wait targets an offset from that
/// one start, so timer overshoot on one message never pushes back the ones after it.
/// </summary>
internal sealed class PlaybackClock
{
    // Task.Delay wakes on the system timer tick, ~15.6 ms on Windows, so it can oversleep by that
    // much. Sleep until this close to the target and spin the rest.
    private static readonly TimeSpan SpinThreshold = TimeSpan.FromMilliseconds(20);

    private readonly long _start = Stopwatch.GetTimestamp();

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);

    /// <summary>Returns once <see cref="Elapsed"/> reaches <paramref name="target"/>; immediately if it already has.</summary>
    public async Task WaitUntilAsync(TimeSpan target, CancellationToken ct)
    {
        var remaining = target - Elapsed;
        if (remaining > SpinThreshold)
            await Task.Delay(remaining - SpinThreshold, ct).ConfigureAwait(false);

        var spinner = new SpinWait();
        while (Elapsed < target)
        {
            ct.ThrowIfCancellationRequested();
            spinner.SpinOnce(sleep1Threshold: -1); // yield, but never Sleep(1): that's a full timer tick
        }
    }
}
