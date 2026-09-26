using System.Globalization;
using Misdirection.Client;

namespace Misdirection.Sender;

/// <summary>Everything the command line can ask for.</summary>
internal sealed record SenderOptions
{
    public string? File { get; init; }
    public string? Port { get; init; }
    public int BaudRate { get; init; } = Protocol.DefaultBaudRate;
    public TimeSpan Delay { get; init; }
    public double Speed { get; init; } = 1;
    public bool IgnoreTiming { get; init; }
    public bool Follow { get; init; }
    public bool FromStart { get; init; }
    public (ushort Width, ushort Height)? ScreenSize { get; init; }
    public bool ContinueOnNack { get; init; }
    public bool MoveBeforeClick { get; init; } = true;
    public bool Ping { get; init; } = true;
    public bool DryRun { get; init; }
    public bool Verbose { get; init; }
    public bool ListPorts { get; init; }
    public bool Help { get; init; }
}

/// <summary>Thrown for a command line that can't be run; the message is shown to the user.</summary>
internal sealed class UsageException(string message) : Exception(message);

internal static class OptionsParser
{
    public const string Usage =
        """
        Usage:
          misdirection-sender <file.msdr> --port <name> [options]
          misdirection-sender <file.msdr> --dry-run
          misdirection-sender <file.msdr> --follow --port <name> [options]
          misdirection-sender --list-ports

        Sends every message in a .msdr protocol file to a misdirection device, in order, at
        the pace recorded in the file's delay records. With --follow, sends each message as it
        is appended to the file instead, like tail -f, until Ctrl+C.

        Options:
          -p, --port <name>       Serial port the device is on, e.g. COM5
          -b, --baud <rate>       Baud rate (default 115200)
          -x, --speed <factor>    Playback speed: 2 is twice as fast, 0.5 half speed (default 1)
              --ignore-timing     Ignore the file's delays and send as fast as --delay allows
          -f, --follow            Keep the file open and send each message as it is appended,
                                  ignoring the file's timing. Skips what the file already
                                  holds unless --from-start is given.
              --from-start        With --follow, send what the file already holds first
          -d, --delay <ms>        Minimum milliseconds between messages (default 0). Applies on
                                  top of the file's timing, and alone with --ignore-timing
                                  or --follow.
          -s, --screen <WxH>      Send SCREEN_SIZE first, e.g. 2560x1440
              --continue-on-nack  Keep sending after the device NACKs (default: stop and PANIC)
              --no-move-before-click
                                  Don't send a MOUSE_MOVE to the last absolute position before
                                  every MOUSE_BUTTONS message (default: send one, so each click
                                  lands where it was recorded)
              --no-ping           Skip the PING handshake before sending and the PING that
                                  confirms delivery afterwards
          -n, --dry-run           Validate the file and list its messages; no port is opened.
                                  With --follow, lists messages as they are appended.
          -v, --verbose           Print each message as it is sent
              --list-ports        List serial ports and exit
          -h, --help              Show this help

        Ctrl+C stops sending and sends PANIC so nothing is left held down.

        Exit codes: 0 sent, 1 error, 2 bad arguments, 3 device NACKed, 130 cancelled.

        """;

    public static SenderOptions Parse(IReadOnlyList<string> args)
    {
        var o = new SenderOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "-?" or "--help":
                    o = o with { Help = true };
                    break;
                case "-p" or "--port":
                    o = o with { Port = Value() };
                    break;
                case "-b" or "--baud":
                    o = o with { BaudRate = PositiveInt(Value(), arg) };
                    break;
                case "-d" or "--delay":
                    var ms = Value();
                    if (!int.TryParse(ms, NumberStyles.None, CultureInfo.InvariantCulture, out var delay))
                        throw new UsageException($"{arg} expects a whole number of milliseconds, got '{ms}'.");
                    o = o with { Delay = TimeSpan.FromMilliseconds(delay) };
                    break;
                case "-x" or "--speed":
                    o = o with { Speed = PositiveDouble(Value(), arg) };
                    break;
                case "--ignore-timing":
                    o = o with { IgnoreTiming = true };
                    break;
                case "-f" or "--follow":
                    o = o with { Follow = true };
                    break;
                case "--from-start":
                    o = o with { FromStart = true };
                    break;
                case "-s" or "--screen":
                    o = o with { ScreenSize = ParseScreenSize(Value()) };
                    break;
                case "--continue-on-nack":
                    o = o with { ContinueOnNack = true };
                    break;
                case "--no-move-before-click":
                    o = o with { MoveBeforeClick = false };
                    break;
                case "--no-ping":
                    o = o with { Ping = false };
                    break;
                case "-n" or "--dry-run":
                    o = o with { DryRun = true };
                    break;
                case "-v" or "--verbose":
                    o = o with { Verbose = true };
                    break;
                case "--list-ports":
                    o = o with { ListPorts = true };
                    break;
                default:
                    if (arg.Length > 1 && arg.StartsWith('-'))
                        throw new UsageException($"Unknown option '{arg}'.");
                    if (o.File is not null)
                        throw new UsageException($"Unexpected argument '{arg}'; give one file to send.");
                    o = o with { File = arg };
                    break;
            }

            string Value()
            {
                if (i + 1 >= args.Count)
                    throw new UsageException($"{arg} needs a value.");
                return args[++i];
            }
        }

        if (o.Help || o.ListPorts) return o;
        if (o.File is null)
            throw new UsageException("No file given.");
        if (o.Port is null && !o.DryRun)
            throw new UsageException("No serial port given. Pass --port (see --list-ports), or --dry-run to only inspect the file.");
        if (o.FromStart && !o.Follow)
            throw new UsageException("--from-start only applies with --follow.");
        if (o.Follow && o.Speed != 1)
            throw new UsageException("--speed has no effect with --follow, which ignores the file's timing.");
        return o;
    }

    private static int PositiveInt(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n == 0)
            throw new UsageException($"{option} expects a positive whole number, got '{value}'.");
        return n;
    }

    private static double PositiveDouble(string value, string option)
    {
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
            || n <= 0 || !double.IsFinite(n))
            throw new UsageException($"{option} expects a positive number, got '{value}'.");
        return n;
    }

    private static (ushort, ushort) ParseScreenSize(string value)
    {
        var parts = value.Split('x', 'X');
        if (parts.Length != 2
            || !ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h))
            throw new UsageException($"--screen expects WIDTHxHEIGHT, e.g. 1920x1080, got '{value}'.");
        if (w is < Protocol.MinScreenDimension or > Protocol.MaxScreenDimension
            || h is < Protocol.MinScreenDimension or > Protocol.MaxScreenDimension)
            throw new UsageException(
                $"--screen dimensions must each be {Protocol.MinScreenDimension}..{Protocol.MaxScreenDimension}, got '{value}'.");
        return (w, h);
    }
}
