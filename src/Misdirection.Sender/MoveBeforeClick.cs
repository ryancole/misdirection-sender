using Misdirection.Client;

namespace Misdirection.Sender;

/// <summary>
/// Inserts a <see cref="MouseMoveMessage"/> before every <see cref="MouseButtonsMessage"/>, repeating
/// the last absolute position the sequence moved to, so a click on the wire always arrives right
/// after a move to where it should land.
/// </summary>
internal static class MoveBeforeClick
{
    /// <summary>
    /// Each inserted move shares the click's <c>At</c>, so it goes out immediately before it. A click
    /// with no known position is left alone: before the first absolute move, or after a
    /// <see cref="MouseMoveRelMessage"/>, which leaves the pointer somewhere the sequence can't name.
    /// Nothing is inserted when the message just before the click already is that move.
    /// </summary>
    public static IReadOnlyList<(TimeSpan At, Message Message)> Apply(IReadOnlyList<(TimeSpan At, Message Message)> messages)
    {
        var result = new List<(TimeSpan At, Message Message)>(messages.Count);
        MouseMoveMessage? position = null;
        foreach (var entry in messages)
        {
            switch (entry.Message)
            {
                case MouseMoveMessage move:
                    position = move;
                    break;
                case MouseMoveRelMessage:
                    position = null;
                    break;
                case MouseButtonsMessage when position is not null
                    && (result.Count == 0 || result[^1].Message != position):
                    result.Add((entry.At, position));
                    break;
            }
            result.Add(entry);
        }
        return result;
    }
}
