namespace Mezhs.Common.RichUi;

// Source positions are zero-based UTF-16 offsets into the original message.
// End is exclusive; nothing in this contract is a frontend event handler.
public sealed record RichSourceSpan(int Start, int End);

public sealed record RichNode(
    string Kind,
    string Id,
    RichSourceSpan SourceSpan,
    string? Text = null,
    string? Name = null,
    IReadOnlyDictionary<string, string>? Attributes = null,
    IReadOnlyList<RichNode>? Children = null);

public sealed record RichDiagnostic(string Code, RichSourceSpan SourceSpan);

// Only validated display nodes may be serialized to a Web frontend.
public sealed record RichDocument(
    int Version,
    bool Valid,
    IReadOnlyList<RichNode> Nodes,
    IReadOnlyList<RichDiagnostic> Diagnostics)
{
    public const int CurrentVersion = 1;
}

// Host-side only. Never serialize command candidates as executable frontend controls.
public sealed record RichCommandCandidate(string Name, string? Body, RichSourceSpan SourceSpan);
public sealed record RichParseResult(RichDocument Document, IReadOnlyList<RichCommandCandidate> Commands);
