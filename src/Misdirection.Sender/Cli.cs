using System.Globalization;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using Misdirection.Client;

namespace Misdirection.Sender;

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int Nacked = 3;
    public const int Cancelled = 130;
}

/// <summary>
/// The program proper: parse arguments, load and validate the file (or start following it), then
/// open the port and send.
/// The port opener is injectable so tests can hand in a client over an in-memory stream.
/// </summary>
internal sealed class Cli(TextWriter stdout, TextWriter stderr, Func<string, int, MisdirectionClient>? openPort = null)
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(1);

    private readonly Func<string, int, MisdirectionClient> _openPort =
        openPort ?? ((port, baud) => MisdirectionClient.OpenSerial(port, baud));

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        SenderOptions options;
        try
        {
            options = OptionsParser.Parse(args);
        }
        catch (UsageException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            stderr.WriteLine("Run with --help for usage.");
            return ExitCodes.Usage;
        }

        if (options.Help)
        {
            stdout.Write(OptionsParser.Usage);
            return ExitCodes.Ok;
        }
        if (options.ListPorts)
            return ListPorts();

        return options.Follow
            ? await FollowAsync(options, ct)
            : await PlayAsync(options, ct);
    }

    private async Task<int> PlayAsync(SenderOptions options, CancellationToken ct)
    {
        // Read the whole file before touching the port, so a malformed file sends nothing at all.
        var file = options.File!;
        IReadOnlyList<(TimeSpan At, Message Message)> messages;
        try
        {
            messages = ReadTimed(file);
        }
        catch (Exception ex) when (ex is ProtocolFileException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: {file}: {ex.Message}");
            return ExitCodes.Error;
        }

        // ReadTimed already consumes the delay records; what's left that the device won't take is
        // anything recorded from the back-channel.
        var skipped = messages.Count(m => !m.Message.IsHostToDevice);
        if (skipped > 0)
        {
            stderr.WriteLine($"warning: skipping {skipped} device-to-host message(s) (PONG/NACK); the device doesn't accept them.");
            messages = [.. messages.Where(m => m.Message.IsHostToDevice)];
        }

        if (options.MoveBeforeClick)
            messages = MoveBeforeClick.Apply(messages);

        var settings = Settings(options);

        stdout.WriteLine(Describe(file, messages, options));

        if (options.DryRun)
        {
            for (var i = 0; i < messages.Count; i++)
                stdout.WriteLine($"{i + 1,6}  {FormatTime(settings.Scheduled(messages[i].At))}  {messages[i].Message}");
            return ExitCodes.Ok;
        }

        return await OpenAndSendAsync(options, settings, messages.ToAsyncEnumerable(), messages.Count, ct);
    }

    /// <summary>
    /// <c>tail -f</c>: reads what the file already holds, then sends each message as it lands, until
    /// Ctrl+C or a NACK. The file's timing is ignored; only <c>--delay</c> paces.
    /// </summary>
    private async Task<int> FollowAsync(SenderOptions options, CancellationToken ct)
    {
        var file = options.File!;
        FileFollower follower;
        try
        {
            follower = new FileFollower(file)
            {
                Skipped = (m, offset) => stderr.WriteLine(
                    $"warning: skipping {m.Type} at offset {offset}; the device doesn't accept device-to-host messages."),
                Truncated = () => stderr.WriteLine($"{file}: file truncated; following from its start."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: {file}: {ex.Message}");
            return ExitCodes.Error;
        }

        using (follower)
        {
            // Read what's there before touching the port, so a file that's already malformed sends nothing.
            IReadOnlyList<Message> existing;
            try
            {
                existing = follower.ReadExisting();
            }
            catch (FollowException ex)
            {
                stderr.WriteLine($"error: {file}: {ex.Message}");
                return ExitCodes.Error;
            }

            var inserter = options.MoveBeforeClick ? new MoveBeforeClick() : null;
            if (options.FromStart)
            {
                stdout.WriteLine($"Following {file} from the start ({existing.Count} message(s) so far). Ctrl+C to stop.");
            }
            else
            {
                // Not sent, but the inserter still learns the last position, so a click that lands
                // later gets a move to it.
                foreach (var m in existing)
                    inserter?.Skip(m);
                stdout.WriteLine($"Following {file}; skipped the {existing.Count} message(s) already in it. Ctrl+C to stop.");
                existing = [];
            }

            var source = Source();

            if (options.DryRun)
            {
                var n = 0;
                try
                {
                    await foreach (var (_, m) in source.WithCancellation(ct))
                        stdout.WriteLine($"{++n,6}  {m}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    stderr.WriteLine($"Stopped after {n} message(s).");
                    return ExitCodes.Cancelled;
                }
                catch (FollowException ex)
                {
                    stderr.WriteLine($"error: {file}: {ex.Message}");
                    return ExitCodes.Error;
                }
            }

            var settings = Settings(options) with { IgnoreTiming = true };
            return await OpenAndSendAsync(options, settings, source, total: null, ct);

            async IAsyncEnumerable<(TimeSpan At, Message Message)> Source([EnumeratorCancellation] CancellationToken token = default)
            {
                foreach (var m in existing)
                {
                    foreach (var toSend in Expand(m))
                        yield return (TimeSpan.Zero, toSend);
                }
                await foreach (var m in follower.FollowAsync(token))
                {
                    foreach (var toSend in Expand(m))
                        yield return (TimeSpan.Zero, toSend);
                }
            }

            IEnumerable<Message> Expand(Message m) => inserter?.Next(m) ?? [m];
        }
    }

    private static SendSettings Settings(SenderOptions options) => new()
    {
        Speed = options.Speed,
        IgnoreTiming = options.IgnoreTiming,
        MinimumGap = options.Delay,
        ScreenSize = options.ScreenSize,
        StopOnNack = !options.ContinueOnNack,
        ConfirmTimeout = options.Ping ? TimeSpan.FromSeconds(2) : null,
    };

    /// <param name="total">How many messages <paramref name="messages"/> holds, or null when following.</param>
    private async Task<int> OpenAndSendAsync(
        SenderOptions options,
        SendSettings settings,
        IAsyncEnumerable<(TimeSpan At, Message Message)> messages,
        int? total,
        CancellationToken ct)
    {
        var port = options.Port!;
        MisdirectionClient client;
        try
        {
            client = _openPort(port, options.BaudRate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            stderr.WriteLine($"error: could not open {port}: {ex.Message}");
            return ExitCodes.Error;
        }

        await using (client)
        {
            try
            {
                return await SendAsync(client, port, messages, total, options, settings, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stderr.WriteLine("Cancelled before sending.");
                return ExitCodes.Cancelled;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                stderr.WriteLine($"error: lost {port}: {ex.Message}");
                return ExitCodes.Error;
            }
            catch (FollowException ex)
            {
                stderr.WriteLine($"error: {options.File}: {ex.Message} Stopped and sent PANIC.");
                return ExitCodes.Error;
            }
        }
    }

    private async Task<int> SendAsync(
        MisdirectionClient client,
        string port,
        IAsyncEnumerable<(TimeSpan At, Message Message)> messages,
        int? total,
        SenderOptions options,
        SendSettings settings,
        CancellationToken ct)
    {
        if (options.Ping)
        {
            byte version;
            try
            {
                version = await client.PingAsync(HandshakeTimeout, ct);
            }
            catch (TimeoutException)
            {
                stderr.WriteLine($"error: no reply to PING on {port} within {HandshakeTimeout.TotalSeconds:0.#}s. " +
                                 "Is the firmware running? (--no-ping skips this check)");
                return ExitCodes.Error;
            }
            if (version != Protocol.Version)
            {
                stderr.WriteLine($"error: device speaks protocol v{version}; this sender speaks v{Protocol.Version}.");
                return ExitCodes.Error;
            }
            stdout.WriteLine($"Connected to {port} (protocol v{version}).");
        }

        Action<int, TimeSpan, Message>? onSent = options.Verbose
            ? (n, at, m) => stdout.WriteLine($"{n,6}  {FormatTime(at)}  {m}")
            : null;

        var result = await MessageSender.SendAsync(client, messages, settings, onSent, ct);

        foreach (var nack in result.Nacks)
            stderr.WriteLine($"NACK {nack.Reason} (after message {nack.SentBefore})");

        var progress = total is { } t ? $"{result.Sent} of {t}" : $"{result.Sent}";
        switch (result.Status)
        {
            case SendStatus.Cancelled:
                stderr.WriteLine($"Cancelled after {progress} message(s); sent PANIC.");
                return ExitCodes.Cancelled;
            case SendStatus.Nacked:
                stderr.WriteLine($"Stopped after {progress} message(s) on NACK; sent PANIC.");
                return ExitCodes.Nacked;
        }

        stdout.WriteLine($"Sent {result.Sent} message(s).");
        if (result.Confirmed == false)
        {
            stderr.WriteLine("error: the device didn't answer the PING sent after the last message, so delivery is unconfirmed.");
            return ExitCodes.Error;
        }
        return result.Nacks.Count > 0 ? ExitCodes.Nacked : ExitCodes.Ok;
    }

    /// <summary>
    /// Reads the file's playback schedule. Opened read-only and sharing both read and write, so a
    /// file a recorder still has open for appending can be played: the recorder flushes whole
    /// frames, and what is read is the file as it stood at open.
    /// </summary>
    private static IReadOnlyList<(TimeSpan At, Message Message)> ReadTimed(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ProtocolFile.ReadTimed(stream, leaveOpen: true);
    }

    private static string Describe(string file, IReadOnlyList<(TimeSpan At, Message Message)> messages, SenderOptions options)
    {
        var text = $"{file}: {messages.Count} message(s)";
        if (messages.Count == 0 || options.IgnoreTiming)
            return text;
        text += $" over {FormatTime(messages[^1].At / options.Speed)}";
        if (options.Speed != 1)
            text += $" at {options.Speed.ToString(CultureInfo.InvariantCulture)}x (recorded {FormatTime(messages[^1].At)})";
        return text;
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";

    private int ListPorts()
    {
        var ports = SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ports.Length == 0)
            stderr.WriteLine("No serial ports found.");
        foreach (var p in ports)
            stdout.WriteLine(p);
        return ExitCodes.Ok;
    }
}
