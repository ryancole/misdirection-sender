#:project ../submodules/misdirection-client/src/Misdirection.Client/Misdirection.Client.csproj

// Writes a .msdr file that moves the mouse around a circle, for checking a device end to end.
// Run through etc\circle.ps1, which then plays the file with the sender.
//
//   dotnet run etc/circle.cs -- <out.msdr> <width> <height> <radius> <loops> <seconds-per-loop> <abs|rel>

using System.Globalization;
using Misdirection.Client;

if (args.Length != 7)
{
    Console.Error.WriteLine("usage: circle.cs <out.msdr> <width> <height> <radius> <loops> <seconds-per-loop> <abs|rel>");
    return 2;
}

var file = args[0];
var width = int.Parse(args[1], CultureInfo.InvariantCulture);
var height = int.Parse(args[2], CultureInfo.InvariantCulture);
var radius = int.Parse(args[3], CultureInfo.InvariantCulture);
var loops = int.Parse(args[4], CultureInfo.InvariantCulture);
var secondsPerLoop = double.Parse(args[5], CultureInfo.InvariantCulture);
var relative = args[6] switch
{
    "abs" => false,
    "rel" => true,
    _ => throw new ArgumentException($"mode must be abs or rel, not {args[6]}"),
};

// ~60 moves a second, like a real pointer.
const int StepsPerSecond = 60;
var steps = Math.Max(8, (int)Math.Round(secondsPerLoop * StepsPerSecond));
var gap = TimeSpan.FromSeconds(secondsPerLoop / steps);
var cx = width / 2.0;
var cy = height / 2.0;

using var writer = ProtocolFileWriter.Create(file);
var count = 0;

if (relative)
{
    // Relative moves can't name a start point, so the circle starts wherever the pointer is and is
    // traced from there: each step is the rounded difference to the next point, with the rounding
    // error carried so the loop closes.
    var (lastX, lastY) = (0, 0);
    for (var i = 1; i <= steps * loops; i++)
    {
        var a = 2 * Math.PI * i / steps;
        var x = (int)Math.Round(radius * (Math.Cos(a) - 1));
        var y = (int)Math.Round(radius * Math.Sin(a));
        writer.WriteDelay(gap);
        writer.Write(new MouseMoveRelMessage((short)(x - lastX), (short)(y - lastY)));
        (lastX, lastY) = (x, y);
        count++;
    }
}
else
{
    for (var i = 0; i <= steps * loops; i++)
    {
        var a = 2 * Math.PI * i / steps;
        var x = (ushort)Math.Clamp(Math.Round(cx + radius * Math.Cos(a)), 0, width - 1);
        var y = (ushort)Math.Clamp(Math.Round(cy + radius * Math.Sin(a)), 0, height - 1);
        if (i > 0)
            writer.WriteDelay(gap);
        writer.Write(new MouseMoveMessage(x, y));
        count++;
    }
}

Console.WriteLine($"Wrote {file}: {count} {(relative ? "relative" : "absolute")} move(s), " +
                  $"{loops} loop(s) of radius {radius} at {secondsPerLoop.ToString(CultureInfo.InvariantCulture)}s each.");
return 0;
