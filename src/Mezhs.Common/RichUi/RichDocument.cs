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

public enum RichTagRole { Container, Leaf, OpaqueLiteral, RawEditor, Command, Marker }
public sealed record RichTagDefinition(
    string Name,
    RichTagRole Role,
    IReadOnlySet<string>? AllowedAttributes,
    // SearchChildren: recursively discover registered nested tags inside this
    // tag's body. False means the complete body is opaque and never searched.
    bool SearchChildren,
    // AllowAsChild: whether this tag may be discovered while traversing any
    // other searchable tag. Independent of SearchChildren and Agent policy.
    bool AllowAsChild)
{
    // Optional specialized behavior; ordinary presentation still uses its role.
    public Func<RichTagContext, RichTagHandling>? Handle { get; init; }
}


public readonly record struct RichTagHandling(int End, RichNode? Display = null);

// Tag handlers request bounded discovery; the parser alone owns scanning.
public sealed class RichTagContext
{
    private readonly RichDocumentParser _parser;
    internal RichTagContext(RichDocumentParser parser, string name, int start,
        int bodyStart, int bodyEnd, int end, bool isRoot,
        IReadOnlyDictionary<string, string> attributes)
    {
        _parser = parser; Name = name; Start = start; BodyStart = bodyStart;
        BodyEnd = bodyEnd; End = end; IsRoot = isRoot; Attributes = attributes;
    }
    public string Name { get; }
    public int Start { get; }
    public int BodyStart { get; }
    public int BodyEnd { get; }
    public int End { get; }
    public bool IsRoot { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public RichTagHandling Discover(Func<RichTagDefinition, bool> eligible) =>
        _parser.DiscoverInOpaque(this, eligible);
}

// Definitions are injected by the owner: UI API supplies base tags,
// Agent API extends them with command tags and handler overrides.
public sealed class RichTagRegistry
{
    private static readonly IReadOnlySet<string> SvgNames = new HashSet<string>(
        ["svg", "g", "rect", "path", "line", "polyline", "polygon",
         "circle", "ellipse", "text", "tspan"], StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, RichTagDefinition> _tags;

    public RichTagRegistry(IEnumerable<RichTagDefinition> definitions, RichTagDefinition svgText)
    {
        _tags = definitions.ToDictionary(tag => tag.Name, StringComparer.Ordinal);
        SvgText = svgText;
    }
    public IEnumerable<RichTagDefinition> Definitions => _tags.Values;
    public RichTagDefinition SvgText { get; }
    public bool TryGet(string name, bool svgScope, out RichTagDefinition definition)
    {
        if (svgScope && name == "text") { definition = SvgText; return true; }
        if (!_tags.TryGetValue(name, out definition!)) return false;
        return svgScope
            ? SvgNames.Contains(name) || !definition.AllowAsChild
            : !SvgNames.Contains(name) || (name is "svg" or "text");
    }
    public bool TryGet(string name, out RichTagDefinition definition) =>
        TryGet(name, false, out definition);
}
