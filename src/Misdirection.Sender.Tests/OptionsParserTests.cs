namespace Misdirection.Sender.Tests;

public class OptionsParserTests
{
    [Fact]
    public void ParsesEveryOption()
    {
        var o = OptionsParser.Parse(
            ["drag.msdr", "-p", "COM5", "--baud", "9600", "-d", "25", "--screen", "2560x1440",
             "--continue-on-nack", "--no-ping", "-v", "--speed", "1.5", "--ignore-timing", "--no-move-before-click"]);

        Assert.Equal("drag.msdr", o.File);
        Assert.Equal("COM5", o.Port);
        Assert.Equal(9600, o.BaudRate);
        Assert.Equal(TimeSpan.FromMilliseconds(25), o.Delay);
        Assert.Equal(((ushort)2560, (ushort)1440), o.ScreenSize);
        Assert.True(o.ContinueOnNack);
        Assert.False(o.Ping);
        Assert.True(o.Verbose);
        Assert.Equal(1.5, o.Speed);
        Assert.True(o.IgnoreTiming);
        Assert.False(o.MoveBeforeClick);
    }

    [Fact]
    public void DefaultsArePingAndStopOnNackWithNoDelay()
    {
        var o = OptionsParser.Parse(["a.msdr", "--port", "COM3"]);

        Assert.True(o.Ping);
        Assert.False(o.ContinueOnNack);
        Assert.Equal(TimeSpan.Zero, o.Delay);
        Assert.Null(o.ScreenSize);
        Assert.Equal(115200, o.BaudRate);
        Assert.Equal(1, o.Speed);
        Assert.False(o.IgnoreTiming);
        Assert.True(o.MoveBeforeClick);
    }

    [Fact]
    public void DryRunNeedsNoPort()
    {
        var o = OptionsParser.Parse(["a.msdr", "--dry-run"]);
        Assert.True(o.DryRun);
        Assert.Null(o.Port);
    }

    [Fact]
    public void FollowAndFromStart()
    {
        var o = OptionsParser.Parse(["a.msdr", "-p", "COM1", "-f", "--from-start"]);
        Assert.True(o.Follow);
        Assert.True(o.FromStart);
        Assert.False(OptionsParser.Parse(["a.msdr", "-p", "COM1"]).Follow);
    }

    [Fact]
    public void HelpAndListPortsNeedNoFile()
    {
        Assert.True(OptionsParser.Parse(["--help"]).Help);
        Assert.True(OptionsParser.Parse(["--list-ports"]).ListPorts);
    }

    [Theory]
    [InlineData(new string[0], "No file")]
    [InlineData(new[] { "a.msdr" }, "No serial port")]
    [InlineData(new[] { "a.msdr", "b.msdr", "-p", "COM1" }, "Unexpected argument 'b.msdr'")]
    [InlineData(new[] { "a.msdr", "--frob" }, "Unknown option '--frob'")]
    [InlineData(new[] { "a.msdr", "--port" }, "--port needs a value")]
    [InlineData(new[] { "a.msdr", "-p", "COM1", "-d", "-5" }, "whole number of milliseconds")]
    [InlineData(new[] { "a.msdr", "-p", "COM1", "-b", "0" }, "positive whole number")]
    [InlineData(new[] { "a.msdr", "-p", "COM1", "-x", "0" }, "positive number")]
    [InlineData(new[] { "a.msdr", "-p", "COM1", "-x", "-2" }, "positive number")]
    [InlineData(new[] { "a.msdr", "-p", "COM1", "-x", "fast" }, "positive number")]
    [InlineData(new[] { "a.msdr", "-p", "COM1", "-s", "1920" }, "WIDTHxHEIGHT")]
    [InlineData(new[] { "a.msdr", "-p", "COM1", "-s", "100x1080" }, "128..7680")]
    [InlineData(new[] { "a.msdr", "-p", "COM1", "--from-start" }, "only applies with --follow")]
    [InlineData(new[] { "a.msdr", "-p", "COM1", "--follow", "-x", "2" }, "--speed has no effect with --follow")]
    public void RejectsBadCommandLines(string[] args, string expected)
    {
        var ex = Assert.Throws<UsageException>(() => OptionsParser.Parse(args));
        Assert.Contains(expected, ex.Message);
    }
}
