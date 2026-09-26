using Misdirection.Client;

namespace Misdirection.Sender;

/// <summary>
/// Inserts a <see cref="MouseMoveMessage"/> before every <see cref="MouseButtonsMessage"/>, repeating
/// the last absolute position the sequence moved to, so a click on the wire always arrives right
/// after a move to where it should land.
/// </summary>
/// <remarks>
/// A click with no known position is left alone: before the first absolute move, or after a
/// <see cref="MouseMoveRelMessage"/>, which leaves the pointer somewhere the sequence can't name.
/// Nothing is inserted when the message just before the click already is that move.
/// </remarks>
internal sealed class MoveBeforeClick
{
    private MouseMoveMessage? _position;
    private Message? _previous;

    /// <summary>
    /// Returns what to send for the next message in the sequence: the message, preceded by a move
    /// when it is a click that needs one.
    /// </summary>
    public IEnumerable<Message> Next(Message message)
    {
        if (message is MouseButtonsMessage && _position is not null && _previous != _position)
            yield return _position;
        Track(message);
        _previous = message;
        yield return message;
    }

    /// <summary>
    /// Takes in a message that won't be sent. A position it moves to still counts, but since it never
    /// reaches the wire, the next click gets its move even if this was that move.
    /// </summary>
    public void Skip(Message message)
    {
        Track(message);
        _previous = null;
    }

    private void Track(Message message)
    {
        switch (message)
        {
            case MouseMoveMessage move:
                _position = move;
                break;
            case MouseMoveRelMessage:
                _position = null;
                break;
        }
    }

    /// <summary>Applies to a whole sequence. Each inserted move shares the click's <c>At</c>, so it goes out immediately before it.</summary>
    public static IReadOnlyList<(TimeSpan At, Message Message)> Apply(IReadOnlyList<(TimeSpan At, Message Message)> messages)
    {
        var inserter = new MoveBeforeClick();
        var result = new List<(TimeSpan At, Message Message)>(messages.Count);
        foreach (var (at, message) in messages)
        {
            foreach (var m in inserter.Next(message))
                result.Add((at, m));
        }
        return result;
    }
}
