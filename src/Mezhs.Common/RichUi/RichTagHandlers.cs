namespace Mezhs.Common.RichUi;

// Registered host-only callbacks. Structural discovery and validation remain
// in RichDocumentParser; Agent Registry/Interpreter alone authorize execution.
public static class RichTagHandlers
{
    public static void Command(RichDocumentParser parser, RichTagContent content)
    {
        if (content.SelfClosing)
        {
            parser.RejectTag($"{content.Definition.Name} cannot be self-closing.");
            return;
        }
        parser.AddCommandCandidate(content.Definition.Name,
            content.Definition.Role == RichTagRole.Command ? content.Content : null,
            content.SourceSpan);
    }

    public static void Escape(RichDocumentParser parser, RichTagContent content)
    {
        if (content.IsDocumentRoot)
            parser.DispatchDirectHandlers(content);
    }

    public static void SvgTag(RichDocumentParser parser, RichTagContent content) =>
        parser.AccountSvgTag(content);

    public static void SvgLiteral(RichDocumentParser parser, string text) =>
        parser.AccountSvgLiteral(text);
}
