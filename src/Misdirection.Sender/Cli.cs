using System.Globalization;
using System.IO.Ports;
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
/// The program proper: parse arguments, load and validate the file, then open the port and send.
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

        // Read the whole file before touching the port, so a malformed file sends nothing at all.
        var file = options.File!;
        IReadOnlyList<(TimeSpan At, Message Message)> messages;
        try
        {
            messages = ProtocolFile.ReadTimed(file);
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

        var settings = new SendSettings
        {
            Speed = options.Speed,
            IgnoreTiming = options.IgnoreTiming,
            MinimumGap = options.Delay,
            ScreenSize = options.ScreenSize,
            StopOnNack = !options.ContinueOnNack,
            ConfirmTimeout = options.Ping ? TimeSpan.FromSeconds(2) : null,
        };

        stdout.WriteLine(Describe(file, messages, options));

        if (options.DryRun)
        {
            for (var i = 0; i < messages.Count; i++)
                stdout.WriteLine($"{i + 1,6}  {FormatTime(settings.Scheduled(messages[i].At))}  {messages[i].Message}");
            return ExitCodes.Ok;
        }

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
                return await SendAsync(client, port, messages, options, settings, ct);
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
        }
    }

    private async Task<int> SendAsync(
        MisdirectionClient client,
        string port,
        IReadOnlyList<(TimeSpan At, Message Message)> messages,
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

        switch (result.Status)
        {
            case SendStatus.Cancelled:
                stderr.WriteLine($"Cancelled after {result.Sent} of {messages.Count} message(s); sent PANIC.");
                return ExitCodes.Cancelled;
            case SendStatus.Nacked:
                stderr.WriteLine($"Stopped after {result.Sent} of {messages.Count} message(s) on NACK; sent PANIC.");
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
