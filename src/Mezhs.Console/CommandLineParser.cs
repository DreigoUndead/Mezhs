namespace Mezhs.Console;

internal abstract record ValueNode;
internal sealed record ScalarNode(string Value) : ValueNode;
internal sealed record ListNode(IReadOnlyList<ValueNode> Items) : ValueNode;

internal sealed class CommandLineParser
{
    private readonly string _input;
    private readonly CommandSyntax _syntax;
    private int _position;

    private CommandLineParser(string input, CommandSyntax syntax)
    {
        _input = input;
        _syntax = syntax;
    }

    public static IReadOnlyList<ValueNode> Parse(string input, CommandSyntax syntax) =>
        new CommandLineParser(input, syntax).ParseValues(null);

    private List<ValueNode> ParseValues(CommandSyntaxToken? terminator)
    {
        var values = new List<ValueNode>();
        while (true)
        {
            SkipWhitespace();
            if (_position >= _input.Length)
            {
                if (terminator is not null)
                    throw Error($"Expected '{terminator.End}'.");
                return values;
            }

            if (terminator is not null && _input[_position] == terminator.End)
            {
                _position++;
                return values;
            }

            if (FindClosingToken(_input[_position]) is { } unexpected)
                throw Error($"Unexpected '{unexpected.End}'.");

            values.Add(ParseValue());
        }
    }

    private ValueNode ParseValue()
    {
        var token = FindOpeningToken(_input[_position]);
        if (token is null)
            return new ScalarNode(ParseWord());

        return token.Type switch
        {
            CommandSyntaxTokenType.Quote => new ScalarNode(ParseQuoted(token)),
            CommandSyntaxTokenType.Collection => ParseCollection(token),
            _ => throw Error($"Unsupported syntax token type '{token.Type}'.")
        };
    }

    private ListNode ParseCollection(CommandSyntaxToken token)
    {
        _position++;
        return new ListNode(ParseValues(token));
    }

    private string ParseQuoted(CommandSyntaxToken token)
    {
        _position++;
        var result = new System.Text.StringBuilder();
        while (_position < _input.Length)
        {
            var c = _input[_position++];
            if (c == token.End)
                return result.ToString();

            if (token.Escape is not null && c == token.Escape)
            {
                if (_position >= _input.Length)
                    throw Error("Unterminated escape sequence.");

                var escaped = _input[_position++];
                if (escaped == token.End || escaped == token.Escape)
                {
                    result.Append(escaped);
                }
                else
                {
                    result.Append(token.Escape.Value);
                    result.Append(escaped);
                }
                continue;
            }

            result.Append(c);
        }

        throw Error($"Expected '{token.End}'.");
    }

    private string ParseWord()
    {
        var start = _position;
        while (_position < _input.Length &&
               !char.IsWhiteSpace(_input[_position]) &&
               FindOpeningToken(_input[_position]) is null &&
               FindClosingToken(_input[_position]) is null)
        {
            _position++;
        }

        if (start == _position)
            throw Error($"Unexpected character '{_input[_position]}'.");
        return _input[start.._position];
    }

    private CommandSyntaxToken? FindOpeningToken(char value) =>
        _syntax.Tokens.FirstOrDefault(x => x.Start == value);

    private CommandSyntaxToken? FindClosingToken(char value) =>
        _syntax.Tokens.FirstOrDefault(x => x.End == value && x.Start != x.End);

    private void SkipWhitespace()
    {
        while (_position < _input.Length && char.IsWhiteSpace(_input[_position]))
            _position++;
    }

    private FormatException Error(string message) => new($"{message} Position {_position}.");
}
