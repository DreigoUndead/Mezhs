namespace Mezhs.Console;

public enum CommandSyntaxTokenType
{
    Quote,
    Collection
}

public sealed record CommandSyntaxToken(
    CommandSyntaxTokenType Type,
    char Start,
    char End,
    char? Escape = null);

public sealed class CommandSyntax
{
    public static CommandSyntax Default { get; } = new([
        new(CommandSyntaxTokenType.Quote, '"', '"', '\\'),
        new(CommandSyntaxTokenType.Collection, '[', ']')
    ]);

    public CommandSyntax(IReadOnlyList<CommandSyntaxToken> tokens) => Tokens = tokens;

    public IReadOnlyList<CommandSyntaxToken> Tokens { get; }
}
