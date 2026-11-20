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

            var after = context.CommandSource?.End ?? tag.End;
            string? body = null;
            if (context.Definition.Role == RichTagRole.Command)
            {
                if (context.CommandSource is { })
                    body = context.CommandBody;
                else
                {
                    if (context.Closer < 0) return new TagHandling(after);
                    body = parser._source[tag.End..context.Closer];
                    after = context.Closer + tag.Name.Length + 3;
                }
            }
            parser.AddCommand(new RichCommandCandidate(tag.Name, body,
                context.CommandSource ?? new RichSourceSpan(tag.Start, after)));
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
                out _, discover: static tag => !tag.AllowAsChild && tag.Handle is not null);

            // Both halves of a split command are found by the same first-level
            // tag finder, then passed to the registered command handler.
            var nested = parser.FindTags(wrapper.End, context.Closer);
            if (parser._commands.Count == before && nested.Count == 1 &&
                nested[0] is { Closer: < 0, Tag.Closing: false, Tag.SelfClosing: false } opening &&
                string.IsNullOrWhiteSpace(parser._source[wrapper.End..opening.Tag.Start]) &&
                string.IsNullOrWhiteSpace(parser._source[opening.Tag.End..context.Closer]) &&
                RichTagRegistry.TryGet(opening.Tag.Name, out var definition) &&
                !definition.AllowAsChild && definition.Role == RichTagRole.Command &&
                definition.Handle is { } runCommand)
            {
                foreach (var next in parser.FindTags(after, context.End))
                {
                    if (next.Tag.Closing && next.Tag.Name == opening.Tag.Name)
                        break; // An unwrapped closing delimiter cannot finish this command.
                    if (next.Tag.Closing || next.Closer < 0 ||
                        next.Tag.Name != wrapper.Name)
                        continue;
                    var closing = parser.FindTags(next.Tag.End, next.Closer);
                    if (closing.Count != 1 || !closing[0].Tag.Closing ||
                        closing[0].Tag.Name != opening.Tag.Name ||
                        !string.IsNullOrWhiteSpace(parser._source[next.Tag.End..closing[0].Tag.Start]) ||
                        !string.IsNullOrWhiteSpace(parser._source[closing[0].Tag.End..next.Closer]))
                        continue;

                    var attributes = ParseAttributes(opening.Tag, definition);
                    ParseAttributes(next.Tag, context.Definition);
                    runCommand(new TagContext(parser, opening.Tag, definition, -1,
                        context.End, context.Depth + 1, null, false, attributes,
                        parser._source[after..next.Tag.Start],
                        new RichSourceSpan(wrapper.Start, next.End)));
                    return new TagHandling(next.End);
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
