namespace Mezhs.Common.RichUi;

// Registered host-only callbacks. Structural discovery and validation remain
// in RichDocumentParser; Agent Registry/Interpreter alone authorize execution.
public static class RichTagHandlers
{
    public static void Command(RichDocumentParser parser, RichTagContent content) =>
        parser.CollectCommand(content);

    public static void Escape(RichDocumentParser parser, RichTagContent content) =>
        parser.ProcessEscapedContent(content);
}
