using Misdirection.Client;

namespace Misdirection.Sender.Tests;

public class MoveBeforeClickTests
{
    private static (TimeSpan At, Message Message)[] Timed(params (int Ms, Message Message)[] entries) =>
        [.. entries.Select(e => (TimeSpan.FromMilliseconds(e.Ms), e.Message))];

    [Fact]
    public void RepeatsLastAbsoluteMoveBeforeEachButtonsMessage()
    {
        var move = new MouseMoveMessage(100, 100);
        var press = new MouseButtonsMessage(MouseButtons.Left);
        var release = new MouseButtonsMessage(MouseButtons.None);
        var input = Timed((0, move), (10, new KeyDownMessage(HidUsage.A)), (20, press), (30, release));

        var output = MoveBeforeClick.Apply(input);

        Assert.Equal(
            Timed((0, move), (10, new KeyDownMessage(HidUsage.A)), (20, move), (20, press), (30, move), (30, release)),
            output);
    }

    [Fact]
    public void DoesNotDuplicateAMoveThatAlreadyPrecedesTheClick()
    {
        var move = new MouseMoveMessage(100, 100);
        var press = new MouseButtonsMessage(MouseButtons.Left);
        var input = Timed((0, move), (5, press));

        var output = MoveBeforeClick.Apply(input);

        Assert.Equal(input, output);
    }

    [Fact]
    public void ClickWithNoKnownPositionIsLeftAlone()
    {
        var press = new MouseButtonsMessage(MouseButtons.Left);
        var input = Timed((0, press), (10, new MouseMoveMessage(1, 1)), (20, new MouseMoveRelMessage(5, 5)), (30, press));

        var output = MoveBeforeClick.Apply(input);

        // Before any move, and after a relative move, the pointer's position isn't known.
        Assert.Equal(input, output);
    }

    [Fact]
    public void UsesTheMostRecentAbsoluteMove()
    {
        var first = new MouseMoveMessage(1, 1);
        var second = new MouseMoveMessage(2, 2);
        var press = new MouseButtonsMessage(MouseButtons.Left);
        var input = Timed((0, first), (10, second), (20, new KeyUpMessage(HidUsage.A)), (30, press));

        var output = MoveBeforeClick.Apply(input);

        Assert.Equal(second, output[^2].Message);
        Assert.Equal(press, output[^1].Message);
        Assert.Equal(output[^1].At, output[^2].At);
    }
}
