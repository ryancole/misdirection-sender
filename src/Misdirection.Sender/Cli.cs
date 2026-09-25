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
        IReadOnlyList<Message> messages;
        try
        {
            messages = ProtocolFile.Read(file);
        }
        catch (Exception ex) when (ex is ProtocolFileException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: {file}: {ex.Message}");
            return ExitCodes.Error;
        }

        var skipped = messages.Count(m => !m.IsHostToDevice);
        if (skipped > 0)
        {
            stderr.WriteLine($"warning: skipping {skipped} device-to-host message(s) (PONG/NACK); the device doesn't accept them.");
            messages = [.. messages.Where(m => m.IsHostToDevice)];
        }

        stdout.WriteLine($"{file}: {messages.Count} message(s)");

        if (options.DryRun)
        {
            for (var i = 0; i < messages.Count; i++)
                stdout.WriteLine($"{i + 1,6}  {messages[i]}");
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
                return await SendAsync(client, port, messages, options, ct);
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
        MisdirectionClient client, string port, IReadOnlyList<Message> messages, SenderOptions options, CancellationToken ct)
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

        var settings = new SendSettings
        {
            Delay = options.Delay,
            ScreenSize = options.ScreenSize,
            StopOnNack = !options.ContinueOnNack,
            ConfirmTimeout = options.Ping ? TimeSpan.FromSeconds(2) : null,
        };
        Action<int, Message>? onSent = options.Verbose
            ? (n, m) => stdout.WriteLine($"{n,6}  {m}")
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
