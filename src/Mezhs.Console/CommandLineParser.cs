namespace Mezhs.Console;

internal abstract record ValueNode;
internal sealed record ScalarNode(string Value) : ValueNode;
internal sealed record ListNode(IReadOnlyList<ValueNode> Items) : ValueNode;

internal sealed class CommandLineParser
{
    private readonly string _input;
    private int _position;

    private CommandLineParser(string input) => _input = input;

    public static IReadOnlyList<ValueNode> Parse(string input) => new CommandLineParser(input).ParseValues(null);

    private List<ValueNode> ParseValues(char? terminator)
    {
        var values = new List<ValueNode>();
        while (true)
        {
            SkipWhitespace();
            if (_position >= _input.Length)
            {
                if (terminator is not null)
                    throw Error($"Expected '{terminator}'.");
                return values;
            }

            if (terminator is not null && _input[_position] == terminator)
            {
                _position++;
                return values;
            }

            values.Add(ParseValue());
        }
    }

    private ValueNode ParseValue()
    {
        return _input[_position] switch
        {
            '[' => ParseList(),
            ']' => throw Error("Unexpected ']'."),
            '"' => new ScalarNode(ParseQuoted()),
            _ => new ScalarNode(ParseWord())
        };
    }

    private ListNode ParseList()
    {
        _position++;
        return new ListNode(ParseValues(']'));
    }

    private string ParseQuoted()
    {
        _position++;
        var result = new System.Text.StringBuilder();
        while (_position < _input.Length)
        {
            var c = _input[_position++];
            if (c == '"')
                return result.ToString();

            if (c == '\\')
            {
                if (_position >= _input.Length)
                    throw Error("Unterminated escape sequence.");
                var escaped = _input[_position++];
                result.Append(escaped switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => escaped
                });
                continue;
            }

            result.Append(c);
        }

        throw Error("Unterminated quoted string.");
    }

    private string ParseWord()
    {
        var start = _position;
        while (_position < _input.Length && !char.IsWhiteSpace(_input[_position]) && _input[_position] is not '[' and not ']')
            _position++;

        if (start == _position)
            throw Error($"Unexpected character '{_input[_position]}'.");
        return _input[start.._position];
    }

    private void SkipWhitespace()
    {
        while (_position < _input.Length && char.IsWhiteSpace(_input[_position]))
            _position++;
    }

    private FormatException Error(string message) => new($"{message} Position {_position}.");
}
