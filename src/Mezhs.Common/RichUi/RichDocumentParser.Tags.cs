using System.Text;

namespace Mezhs.Common.RichUi;

public sealed partial class RichDocumentParser
{
    // Registered per-tag functions; the generic parser only tokenizes,
    // validates nesting and attributes, then invokes the tag's handler.
    internal static class TagHandlers
    {
        internal static TagHandling Command(TagContext context)
        {
            var parser = context.Parser;
            var tag = context.Tag;
            if (tag.SelfClosing)
                throw new RichDocumentException($"{tag.Name} cannot be self-closing.");

            var after = tag.End;
            string? body = null;
            if (context.Definition.Role == RichTagRole.Command)
            {
                if (context.Closer < 0) return new TagHandling(after);
                body = parser._source[tag.End..context.Closer];
                after = context.Closer + tag.Name.Length + 3;
            }
            parser.AddCommand(new RichCommandCandidate(tag.Name, body,
                new RichSourceSpan(tag.Start, after)));
            return new TagHandling(after);
        }

        // Root escape searches for registered root-only command tags and invokes
        // their normal handlers. The remaining contents are always literal.
        internal static TagHandling Escape(TagContext context)
        {
            var parser = context.Parser;
            var wrapper = context.Tag;
            var after = context.Closer + wrapper.Name.Length + 3;

            if (context.Parent is not null || context.SvgScope)
                return Literal(parser, context, after, parser._source[wrapper.End..context.Closer]);

            var before = parser._commands.Count;
            parser.ReadChildren(wrapper.End, context.Closer, null, context.Depth + 1,
                out _, commandsOnly: true);

            // The model may put each command delimiter in a separate escape.
            // Match those same registered command definitions; preserve the
            // original source span as one unit for the Agent content projector.
            var opening = wrapper.End;
            while (opening < context.Closer && char.IsWhiteSpace(parser._source[opening]))
                opening++;
            if (parser._commands.Count == before &&
                parser.TryTag(opening, context.Closer, out var opener) &&
                !opener.Closing && !opener.SelfClosing &&
                string.IsNullOrWhiteSpace(parser._source[opener.End..context.Closer]) &&
                RichTagRegistry.TryGet(opener.Name, out var definition) &&
                !definition.AllowAsChild && definition.Role == RichTagRole.Command)
            {
                var closing = parser.FindOpaqueClosing(opener.Name, after, context.End);
                var wrapperStart = closing > after
                    ? parser._source.LastIndexOf('<', closing - 1) : -1;
                if (wrapperStart >= after &&
                    parser.TryTag(wrapperStart, context.End, out var next) &&
                    !next.Closing && !next.SelfClosing && next.Name == wrapper.Name &&
                    next.End <= closing &&
                    string.IsNullOrWhiteSpace(parser._source[next.End..closing]))
                {
                    var wrapperClose = parser.FindOpaqueClosing(wrapper.Name, closing, context.End);
                    var commandEnd = closing + opener.Name.Length + 3;
                    if (wrapperClose >= commandEnd &&
                        string.IsNullOrWhiteSpace(parser._source[commandEnd..wrapperClose]))
                    {
                        ParseAttributes(opener, definition);
                        ParseAttributes(next, context.Definition);
                        var fullEnd = wrapperClose + wrapper.Name.Length + 3;
                        parser.AddCommand(new RichCommandCandidate(opener.Name,
                            parser._source[after..wrapperStart],
                            new RichSourceSpan(wrapper.Start, fullEnd)));
                        return new TagHandling(fullEnd);
                    }
                }
            }

            if (parser._commands.Count == before + 1 &&
                parser._commands[before].SourceSpan is { } only &&
                only.Start == wrapper.End && only.End == context.Closer)
            {
                parser._commands[before] = parser._commands[before] with {
                    SourceSpan = new RichSourceSpan(wrapper.Start, after) };
                return new TagHandling(after);
            }

            var visible = new StringBuilder();
            var keptFrom = wrapper.End;
            foreach (var command in parser._commands.Skip(before))
            {
                visible.Append(parser._source, keptFrom, command.SourceSpan.Start - keptFrom);
                keptFrom = command.SourceSpan.End;
            }
            visible.Append(parser._source, keptFrom, context.Closer - keptFrom);
            return Literal(parser, context, after, visible.ToString());
        }

        private static TagHandling Literal(RichDocumentParser parser, TagContext context,
            int end, string body) =>
            new(end, new RichNode("literal", $"{context.Tag.Start}:raw",
                new RichSourceSpan(context.Tag.Start, end), Text: body,
                Name: context.Tag.Name, Attributes: context.Attributes));
    }
}
