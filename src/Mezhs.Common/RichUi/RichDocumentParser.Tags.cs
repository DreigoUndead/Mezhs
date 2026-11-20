using System.Text;

namespace Mezhs.Common.RichUi;

public sealed partial class RichDocumentParser
{
    internal static class TagHandlers
    {
        internal static TagHandling Escape(TagContext context)
        {
            var parser = context.Parser;
            var wrapper = context.Tag;
            var end = context.Closer + wrapper.Name.Length + 3;
            if (context.Parent is not null || context.SvgScope)
                return Literal(context, end, parser._source[wrapper.End..context.Closer]);

            var before = parser._commands.Count;
            for (var at = wrapper.End; at < context.Closer;)
            {
                at = parser._source.IndexOf('<', at);
                if (at < 0 || at >= context.Closer) break;
                if (!parser.TryTag(at, context.Closer, out var tag)) { at++; continue; }
                if (tag.Closing) { at = tag.End; continue; }

                if (!RichTagRegistry.TryGet(tag.Name, out var definition) ||
                    definition.AllowAsChild ||
                    definition.Role is not (RichTagRole.Command or RichTagRole.Marker))
                {
                    // Other complete elements are inert, including their descendants.
                    if (tag.SelfClosing) { at = tag.End; continue; }
                    var close = definition is { SearchChildren: false }
                        ? parser.FindOpaqueClosing(tag.Name, tag.End, context.Closer)
                        : parser.FindContainerClose(tag.Name, tag.End, context.Closer);
                    at = close < 0 ? context.Closer : close + tag.Name.Length + 3;
                    continue;
                }

                // Reuse the normal command grammar and budget, never execute here.
                var commandEnd = parser.ReadCommand(tag, definition, context.Closer);
                at = commandEnd < 0 ? tag.End : commandEnd;
            }

            // A wrapper containing only one complete command is wholly invisible;
            // surrounding Markdown remains one uninterrupted source span.
            if (parser._commands.Count == before + 1 &&
                parser._commands[before].SourceSpan is { } only &&
                only.Start == wrapper.End && only.End == context.Closer)
            {
                parser._commands[before] = parser._commands[before] with {
                    SourceSpan = new RichSourceSpan(wrapper.Start, end) };
                return new TagHandling(end);
            }

            var visible = new StringBuilder();
            var from = wrapper.End;
            foreach (var command in parser._commands.Skip(before))
            {
                visible.Append(parser._source, from, command.SourceSpan.Start - from);
                from = command.SourceSpan.End;
            }
            visible.Append(parser._source, from, context.Closer - from);
            return Literal(context, end, visible.ToString());
        }

        private static TagHandling Literal(TagContext context, int end, string value) =>
            new(end, new RichNode("literal", $"{context.Tag.Start}:raw",
                new RichSourceSpan(context.Tag.Start, end), Text: value,
                Name: context.Tag.Name, Attributes: context.Attributes));
    }
}
