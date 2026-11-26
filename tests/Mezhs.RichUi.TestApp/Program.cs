using System.Text.Json;
using Mezhs.Common.RichUi;
using Mezhs.Agent.Commands;

if (args.Contains("--parse-stdin-results"))
{
    Console.InputEncoding = System.Text.Encoding.UTF8;
    var inputs = JsonSerializer.Deserialize<string[]>(Console.In.ReadToEnd()) ?? [];
    var results = inputs.Select(source => new RichDocumentParser(Registry.RichTags).Parse(source)).ToArray();
    Console.Write(JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

if (args.Contains("--parse-stdin"))
{
    // The Windows host console may default to an OEM code page, even when
    // Node feeds UTF-8 JSON bytes. Preserve non-ASCII radio choice payloads.
    Console.InputEncoding = System.Text.Encoding.UTF8;
    var inputs = JsonSerializer.Deserialize<string[]>(Console.In.ReadToEnd()) ?? [];
    var results = inputs.Select(source => new RichDocumentParser(Registry.RichTags).Parse(source).Document).ToArray();
    Console.Write(JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

var checks = new (string Name, string Source, bool Valid, string[] Commands)[]
{
    ("Plain Markdown", "# Hello\n\n**Strong** and [link](https://example.com)", true, []),
    ("Nested box and text", "<box gap={2}>\n<text>hello</text>\n</box>", true, []),
    ("Unknown presentation attribute is inert", "<text futureTone=\"violet\" strong>Visible</text>", true, []),
    ("Unsafe HTML attribute rejected", "<badge style=\"color:red\">No</badge>", true, []),
    ("Unsafe event attribute blocks entire batch", "<box onClick=\"alert(1)\"><SH>echo NO</SH></box>", true, []),
    ("Unknown SVG attribute rejected", "<svg futureTheme=\"violet\"><rect width=\"5\"/></svg>", true, []),
    ("Citation ID supported", "<Cite ref=\"turn0search0\"/>", true, []),
    ("Citation arbitrary metadata rejected", "<Cite futureSource=\"javascript:evil\"/>", true, []),
    ("Inline nested tags", "<text>Before <badge>status</badge> and <Link url=\"https://example.com\">link</Link> after</text>", true, []),
    ("Inline text rejects block child", "<text>Before <box>block</box> after</text>", true, []),
    ("Inline caption rejects list", "<caption>Start\n\n- One\n- Two\n</caption>", true, []),
    ("Rich Link rejects nested links", "<Link url=\"https://a.test\">Outer <Link url=\"https://b.test\">Inner</Link></Link>", true, []),
    ("Inline SH", "before <SH>echo one</SH> after", true, ["SH"]),
    ("Multiline SH preserves body bytes", "<SH>\r\necho first\r\n<DONE>\r\n</SH>\r\n<DONE>", true, ["SH","DONE"]),
    ("Adjacent tags", "<SH>echo A</SH><ATTACH>file.txt</ATTACH><DONE>", true, ["SH","ATTACH","DONE"]),
    ("Inline ATTACH", "prefix <ATTACH>file.txt</ATTACH> suffix", true, ["ATTACH"]),
    ("Inline rich UI", "before <box><text>hi</text></box> after", true, []),
    ("Nested paired rich UI", "<box><box>inner</box>outer</box>", true, []),
    ("Unclosed SH is ordinary text", "before <SH>echo incomplete", true, []),
    ("Unclosed rich UI is ordinary text", "before <box>not finished", true, []),
    ("Unclosed SH before independent DONE", "<SH>not closed <DONE>", true, ["DONE"]),
    ("Unclosed escape still contains matched SH", "<escape><SH>echo YES</SH>", true, ["SH"]),
    ("Command inside text is inert", "<text>Hi <SH>echo YES</SH> there</text>", true, []),
    ("Command inside Link is inert", "<Link url=\"https://example.com\"><ATTACH>file.txt</ATTACH></Link>", true, []),
    ("Command inside CodeBlock is inert", "<CodeBlock>before <SH>echo YES</SH> after</CodeBlock>", true, []),
    ("Same-line CodeBlock renders without a delimiter-line requirement", "Before <CodeBlock language=\"text\">raw source</CodeBlock> after", true, []),
    ("Same-line CodeBlock before SH remains valid", "<CodeBlock language=\"text\">raw</CodeBlock><SH>echo OK</SH>", true, ["SH"]),
    ("Same-line WritingBlock remains opaque", "Before <WritingBlock variant=\"document\">raw <SH>INERT</SH></WritingBlock> after", true, []),
    ("Same-line WritingBlock before root SH", "<WritingBlock variant=\"document\">raw</WritingBlock><SH>echo OK</SH>", true, ["SH"]),
    ("Same-line escape recognizes its direct shell", "Before <escape>raw <SH>RUN</SH></escape> after", true, ["SH"]),
    ("Same-line registered containers match", "Before <box><text>inside</text></box> after", true, []),
    ("Same-line registered inline element matches", "Before <text strong>inside</text> after", true, []),
    ("Direct command inside escape executes", "<escape><SH>echo YES</SH></escape>", true, ["SH"]),
    ("Escape recognizes shell attach and final DONE", "<escape><SH>echo YES</SH><ATTACH>file.txt</ATTACH><DONE></escape>", true, ["SH","ATTACH","DONE"]),
    ("Escape commands interleave with root commands", "<SH>before</SH><escape><ATTACH>a.txt</ATTACH></escape><SH>after</SH>", true, ["SH","ATTACH","SH"]),
    ("Escape literal UI remains inert", "<escape>before <text strong>literal</text> after</escape>", true, []),
    ("Escape nested known UI commands are inert", "<escape><box>raw <SH>NO</SH></box><SH>YES</SH></escape>", true, ["SH"]),
    ("Escape nested unknown UI commands are inert", "<escape><Widget><SH>NO</SH></Widget><SH>YES</SH></escape>", true, ["SH"]),
    ("Escape nested editor commands are inert", "<escape><CodeBlock><SH>NO</SH></CodeBlock><SH>YES</SH></escape>", true, ["SH"]),
    ("Escape malformed nested UI and SVG keep commands inert", "<escape><box onClick=\"bad\"><SH>NO</SH></box><svg><SH>NO2</SH></svg><Widget><SH>NO3</SH></Widget><SH>YES</SH></escape>", true, ["SH"]),
    ("Escape incomplete SH is literal", "<escape>pre <SH>unclosed</escape>", true, []),
    ("Escape invalid shell attributes reject batch", "<escape><SH bad=\"value\">NO</SH></escape><SH>AFTER</SH>", false, []),
    ("Escape DONE before following command rejects batch", "<escape><DONE></escape><SH>AFTER</SH>", false, []),
    ("Command inside Markdown fence", "~~~\n<SH>echo YES</SH>\n~~~", true, ["SH"]),
    ("Command inside inline code", "`<SH>echo YES</SH>`", true, ["SH"]),
    ("Command inside Markdown comment", "<!-- <SH>echo YES</SH> -->", true, ["SH"]),
    ("Command inside quote", "> <SH>echo YES</SH>", true, ["SH"]),
    ("Command inside SVG is inert", "<svg viewBox=\"0 0 20 20\"><SH>echo YES</SH><circle r=\"2\"/></svg>", true, []),
    ("Opaque command bodies", "<SH>before <ATTACH>nested</ATTACH></SH>", true, ["SH"]),
    ("SH is not a box child", "<box><SH>echo NO</SH></box>", true, []),
    ("ATTACH is not a text child", "<text>pre <ATTACH>file.txt</ATTACH> post</text>", true, []),
    ("Disallowed SH cannot close its parent", "<box><SH>inside </box> still inert</SH>outer</box>", true, []),
    ("Root command after layout", "<box>layout</box><SH>echo YES</SH>", true, ["SH"]),
    ("Unmatched closing tag ignored", "before </SH> after", true, []),
    ("Empty SH parsed", "<SH></SH>", true, ["SH"]),
    ("DONE must be final", "<DONE><SH>echo NO</SH>", false, []),
    ("Duplicate DONE rejected", "<DONE><DONE>", false, []),
    ("Unknown complete tag renders a diagnostic", "<UNKNOWN>text</UNKNOWN>", true, []),
    ("Unknown tag with inert attributes", "<widget onClick=\"alert(1)\">text</widget>", true, []),
    ("Unknown wrapper with registered command", "<widget>pre <SH>echo OK</SH> post</widget>", true, []),
    ("Unknown wrapper with registered rich child", "<widget>pre <text strong>rich</text> post</widget>", true, []),
    ("Unknown XML example before registered command", "~~~xml\n<widget>example</widget>\n~~~\n<SH>echo OK</SH>", true, ["SH"]),
    ("Registered SVG child outside SVG still rejected", "<path d=\"M0 0\"/>", true, []),
    ("Raw script outside Markdown rejected", "<script>alert(1)</script>", true, []),
    ("Raw script inside Markdown fence is an inert diagnostic", "~~~\n<script>alert(1)</script>\n~~~", true, []),
    ("Bounded object literal border", "<box border={{size:2,color:\"danger\"}}>Red</box>", true, []),
    ("Unknown paired wrapper keeps nested SH inert", "<Widget>inert <SH>echo NO</SH></Widget><SH>echo YES</SH>", true, ["SH"]),
    ("Invalid CodeBlock attributes keep nested SH inert", "<CodeBlock invalid=\"bad\">raw <SH>echo NO</SH></CodeBlock><SH>echo YES</SH>", true, ["SH"]),
    ("Markdown pipe table", "| A | B |\n|:---|---:|\n| One | 2 |", true, []),
};
var failures = 0;
static IEnumerable<RichNode> Walk(RichNode node)
{
    yield return node;
    if (node.Children is null) yield break;
    foreach (var child in node.Children)
        foreach (var descendant in Walk(child))
            yield return descendant;
}
foreach (var (name, source, valid, commands) in checks)
{
    var result = new RichDocumentParser(Registry.RichTags).Parse(source);
    var names = result.Commands.Select(c => c.Name).ToArray();
    var pass = result.Document.Valid == valid && names.SequenceEqual(commands);
    if (name == "Multiline SH preserves body bytes")
        pass &= result.Commands.FirstOrDefault()?.Body == "\r\necho first\r\n<DONE>\r\n";
    if (name == "Nested box and text")
        pass &= result.Document.Nodes.Any(n => n.Kind == "element" && n.Name == "box" &&
            n.Children?.Any(c => c.Kind == "paragraph" && c.Children?.Any(t =>
                t.Kind == "element" && t.Name == "text" &&
                t.Children?.Any(child => child.Text == "hello") == true) == true) == true);
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {name}; valid={result.Document.Valid}; commands={string.Join(",", names)}; diagnostics={string.Join(",", result.Document.Diagnostics.Select(d => d.Code))}");
    if (!pass) failures++;
}
// Unknown wrappers intentionally preserve functional registered descendants,
// but they must not bypass nested-interactive normalization.
var unknownInteractiveSource = "<Widget><Link url=\"https://outer.test\">outer " +
    "<Link url=\"https://inner.test\">inner</Link></Link></Widget>" +
    "<SH>ROOT</SH>";
var unknownInteractive = new RichDocumentParser(Registry.RichTags).Parse(unknownInteractiveSource);
var unknownInteractivePass = unknownInteractive.Document.Valid &&
    unknownInteractive.Commands.Select(c => c.Body).SequenceEqual(["ROOT"]) &&
    unknownInteractive.Document.Nodes.SelectMany(Walk).Any(n =>
        n.Kind == "unknown" && n.Attributes is { } attrs &&
        attrs.TryGetValue("reason", out var reason) && reason == "Nested interactive content");
Console.WriteLine($"{(unknownInteractivePass ? "PASS" : "FAIL")}: Unknown-wrapper UI descendants still reject nested interactive content");
if (!unknownInteractivePass) failures++;

// Escape commands retain original source ranges while non-commands stay literal.
var escapeSource = "pre <escape>alpha <text>literal</text> <SH>\r\necho ONE\r\n</SH> beta <ATTACH>file.txt</ATTACH><DONE></escape> post";
var escapeParsed = new RichDocumentParser(Registry.RichTags).Parse(escapeSource);
var escapeLiteral = escapeParsed.Document.Nodes.SelectMany(Walk)
    .SingleOrDefault(n => n.Kind == "literal" && n.Name == "escape");
var escapePass = escapeParsed.Document.Valid &&
    escapeParsed.Commands.Select(n => n.Name).SequenceEqual(["SH", "ATTACH", "DONE"]) &&
    escapeParsed.Commands[0].Body == "\r\necho ONE\r\n" &&
    escapeParsed.Commands.All(n => escapeSource[n.SourceSpan.Start..n.SourceSpan.End].StartsWith("<" + n.Name, StringComparison.Ordinal)) &&
    escapeLiteral?.Text == "alpha <text>literal</text>  beta ";
Console.WriteLine($"{(escapePass ? "PASS" : "FAIL")}: Escape literal projection, exact command spans and preserved command bodies");
if (!escapePass) failures++;

// Only a document-root escape can grant its direct command children eligibility.
// All other escape ancestry is inert, including unknown, invalid and raw wrappers.
var escapeContexts = new (string Name, string Source, string[] Commands)[]
{
    ("Escape inside known UI", "<box><escape><SH>BOX</SH></escape><SH>INERT</SH></box>", []),
    ("Escape inside unknown UI", "<Widget><escape><SH>UNKNOWN</SH></escape></Widget>", []),
    ("Escape inside raw code editor", "<CodeBlock>raw <escape><SH>CODE</SH></escape></CodeBlock>", []),
    ("Escape inside raw writing editor", "<WritingBlock>raw <escape><ATTACH>file.txt</ATTACH></escape></WritingBlock>", []),
    ("Escape inside invalid UI", "<box onClick=\"bad\"><escape><SH>BAD_BOX</SH></escape></box>", []),
    ("Escape inside invalid SVG", "<svg><escape><SH>SVG</SH></escape></svg>", []),
    ("Escape nested inside escape", "<escape><escape><SH>NESTED</SH></escape></escape>", []),
    ("Escape protects a parent closer inside command body", "<box><escape><SH>echo </box> literal</SH></escape><SH>INERT</SH></box>", []),
    ("Escape protects fake closer in nested literal wrapper", "<escape><Widget>text </escape> <SH>INERT</SH></Widget><SH>DIRECT</SH></escape>", ["SH"]),
    ("Opaque editor with nested escape skips direct shell", "<CodeBlock><SH>INERT</SH><escape><SH>DIRECT</SH></escape></CodeBlock>", []),
    ("Invalid escape inside raw editor stays inert", "<CodeBlock><escape onClick=\"invalid\"><SH>INERT</SH></escape></CodeBlock>", []),
    ("Invalid nested escape does not grant command authority", "<escape><escape onClick=\"invalid\"><SH>INERT</SH></escape><SH>DIRECT</SH></escape>", ["SH"]),
    ("Escape inside opaque editor nested through unknown", "<CodeBlock><Widget><escape><ATTACH>path.txt</ATTACH></escape></Widget></CodeBlock>", []),

    ("Escape nested under box inside escape", "<escape><box><escape><SH>INNER</SH></escape><SH>INERT</SH></box><SH>OUTER</SH></escape>", ["SH"]),
    ("Other nested parents stay inert", "<escape><box><SH>INERT</SH></box><SH>DIRECT</SH></escape>", ["SH"]),
    ("Direct root escape with nested command and final marker", "<escape><box><DONE><ATTACH>INERT</ATTACH></box><SH>DIRECT</SH><DONE></escape>", ["SH", "DONE"]),
    ("Unknown wrapper cannot promote root-only ATTACH", "<Widget><ATTACH>no.txt</ATTACH></Widget><escape><ATTACH>yes.txt</ATTACH></escape>", ["ATTACH"]),
    ("Invalid root escape never grants direct command", "<escape onClick=\"bad\"><SH>INERT</SH></escape><SH>ROOT</SH>", ["SH"]),
    ("Direct escape and root command source order", "<escape><ATTACH>a.txt</ATTACH></escape><SH>SECOND</SH><escape><DONE></escape>", ["ATTACH", "SH", "DONE"]),
};
foreach (var (name, source, expected) in escapeContexts)
{
    var parsed = new RichDocumentParser(Registry.RichTags).Parse(source);
    var actual = parsed.Commands.Select(c => c.Name).ToArray();
    var passed = parsed.Document.Valid && actual.SequenceEqual(expected);
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {name}; commands={string.Join(",", actual)}");
    if (!passed) failures++;
}
// Delimiters inside command bodies must never alter outer structural pairing.
var escapedParentCloserSource = "<box><escape><SH>echo </box> literal</SH></escape><SH>INERT</SH></box>";
var escapedParentCloser = new RichDocumentParser(Registry.RichTags).Parse(escapedParentCloserSource);
var escapedParentCloserPass = escapedParentCloser.Document.Valid &&
    escapedParentCloser.Commands.Count == 0 &&
    escapedParentCloser.Document.Nodes.SelectMany(Walk)
        .Any(n => n.Kind == "element" && n.Name == "box");
Console.WriteLine($"{(escapedParentCloserPass ? "PASS" : "FAIL")}: Nested escape protects ancestor closer without granting execution");
if (!escapedParentCloserPass) failures++;

var rawEscapeSource = "<CodeBlock>pre <escape><SH>HIDDEN</SH></escape> post</CodeBlock>";
var rawEscape = new RichDocumentParser(Registry.RichTags).Parse(rawEscapeSource);
var rawEscapeText = rawEscape.Document.Nodes.SelectMany(Walk)
    .FirstOrDefault(n => n.Kind == "raw" && n.Name == "CodeBlock")?.Text;
var rawEscapePass = rawEscape.Document.Valid && rawEscape.Commands.Count == 0 &&
    rawEscapeText == "pre <escape><SH>HIDDEN</SH></escape> post";
Console.WriteLine($"{(rawEscapePass ? "PASS" : "FAIL")}: Opaque editor preserves all inert nested command source");
if (!rawEscapePass) failures++;
var fakeCloserSource = "<escape><SH>echo </escape> literal</SH><ATTACH>file.txt</ATTACH></escape>";
var fakeCloserResult = new RichDocumentParser(Registry.RichTags).Parse(fakeCloserSource);
var fakeCloserPass = fakeCloserResult.Document.Valid &&
    fakeCloserResult.Commands.Select(c => c.Name).SequenceEqual(["SH", "ATTACH"]) &&
    fakeCloserResult.Commands[0].Body == "echo </escape> literal" &&
    fakeCloserResult.Commands.All(c =>
        fakeCloserSource[c.SourceSpan.Start..c.SourceSpan.End].StartsWith("<" + c.Name, StringComparison.Ordinal));
Console.WriteLine($"{(fakeCloserPass ? "PASS" : "FAIL")}: Command body does not close ancestor escape");
if (!fakeCloserPass) failures++;

// Regression: the rendered tree, not only VisibleContent, must keep Markdown flow.
var shellOpen = string.Concat((char)60, "SH", (char)62);
var shellClose = string.Concat((char)60, "/SH", (char)62);
var shellTag = shellOpen + "echo HIDDEN" + shellClose;
var inlineSource = "Before " + shellTag + " after";
var inlineDoc = new RichDocumentParser(Registry.RichTags).Parse(inlineSource);
var paragraph = inlineDoc.Document.Nodes.SingleOrDefault();
var inlineOk = inlineDoc.Document.Valid && inlineDoc.Commands.Count == 1 &&
    inlineDoc.Commands[0].Body == "echo HIDDEN" && paragraph?.Kind == "paragraph" &&
    paragraph.Children?.Count == 2 && paragraph.Children[0].Text == "Before " &&
    paragraph.Children[1].Text == " after" &&
    paragraph.Children[0].SourceSpan.End == inlineSource.IndexOf(shellOpen, StringComparison.Ordinal) &&
    paragraph.Children[1].SourceSpan.Start == inlineSource.IndexOf(" after", StringComparison.Ordinal);
// Generic syntax layer: literal object attributes, unknown envelopes, and pipes.
var objectDoc = new RichDocumentParser(Registry.RichTags).Parse("<box border={{size:2,color:\"danger\"}}>Red</box>");
var objectJson = objectDoc.Document.Nodes.SelectMany(Walk).FirstOrDefault(n => n.Name == "box")?
    .Attributes?.GetValueOrDefault("border");
var objectPass = objectDoc.Document.Valid && objectJson?.StartsWith("json:", StringComparison.Ordinal) == true &&
    Uri.UnescapeDataString(objectJson[5..]).Contains("\"danger\"", StringComparison.Ordinal);
Console.WriteLine($"{(objectPass ? "PASS" : "FAIL")}: Bounded JSX object literal is serialized as inert JSON");
if (!objectPass) failures++;
var dynamicDoc = new RichDocumentParser(Registry.RichTags).Parse("<box border={{size:alert(1)}}>unsafe</box>");
var dynamicPass = dynamicDoc.Document.Valid && dynamicDoc.Commands.Count == 0 &&
    dynamicDoc.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "unknown" && n.Name == "box");
Console.WriteLine($"{(dynamicPass ? "PASS" : "FAIL")}: Dynamic expression falls back to inert unknown control");
if (!dynamicPass) failures++;
var unknownEnvelope = new RichDocumentParser(Registry.RichTags).Parse("<Widget><SH>inert</SH></Widget><SH>root</SH>");
var unknownPass = unknownEnvelope.Document.Valid && unknownEnvelope.Commands.Count == 1 &&
    unknownEnvelope.Commands[0].Body == "root" &&
    unknownEnvelope.Document.Nodes.Any(n => n.Kind == "unknown" && n.Name == "Widget");
Console.WriteLine($"{(unknownPass ? "PASS" : "FAIL")}: Unknown wrapper preserves root-only command boundary");
if (!unknownPass) failures++;
var tableDoc = new RichDocumentParser(Registry.RichTags).Parse("| A | B |\\n|---|---|\\n| 1 | 2 |".Replace("\\n", "\n"));
var tablePass = tableDoc.Document.Valid && tableDoc.Document.Nodes.SelectMany(Walk)
    .Any(n => n.Kind == "table" && n.Children?.Any(c => c.Kind == "tableRow") == true);
Console.WriteLine($"{(tablePass ? "PASS" : "FAIL")}: Pipe table remains a generic Markdown syntax transformation");
if (!tablePass) failures++;
var htmlWrapperSource = "<widget>\\n<text>inside</text>\\n</widget>\\n<SH>ROOT</SH>".Replace("\\n", "\n");
var htmlWrapperDoc = new RichDocumentParser(Registry.RichTags).Parse(htmlWrapperSource);
var htmlWrapperPass = htmlWrapperDoc.Document.Valid && htmlWrapperDoc.Commands.Count == 1 &&
    htmlWrapperDoc.Commands[0].Body == "ROOT" &&
    htmlWrapperDoc.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "unknown" && n.Name == "widget");
Console.WriteLine($"{(htmlWrapperPass ? "PASS" : "FAIL")}: Multiline unknown HTML wrapper cannot discard root commands");
if (!htmlWrapperPass) failures++;
var commentSource = "<!--\\n<text>hello</text>\\n-->\\n<SH>ROOT</SH>".Replace("\\n", "\n");
var commentDoc = new RichDocumentParser(Registry.RichTags).Parse(commentSource);
var commentPass = commentDoc.Document.Valid && commentDoc.Commands.Count == 1 &&
    commentDoc.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "element" && n.Name == "text");
Console.WriteLine($"{(commentPass ? "PASS" : "FAIL")}: HTML literal lowering restores rich elements");
if (!commentPass) failures++;
var citeFenceTag = "<Cite ref=\"turn0search0\"/>";
var citeFenceInputs = new[] {
    "~~~jsx\n" + citeFenceTag,
    "~~~jsx\n" + citeFenceTag + "\n",
    "~~~jsx\n" + citeFenceTag + "\n~~~",
    "~~~jsx\n" + citeFenceTag + "\n<Widget/>\n~~~",
    "Before\n\n~~~jsx\n" + citeFenceTag + "\n<Cite ref={foo}/>\n~~~\n\nAfter"
};
var citeFencesPass = citeFenceInputs.All(source => {
    var parsed = new RichDocumentParser(Registry.RichTags).Parse(source);
    var cites = parsed.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "element" && n.Name == "Cite").ToArray();
    var start = source.IndexOf(citeFenceTag, StringComparison.Ordinal);
    return parsed.Document.Valid && parsed.Commands.Count == 0 && cites.Length == 1 &&
        cites[0].SourceSpan.Start == start && cites[0].SourceSpan.End == start + citeFenceTag.Length;
});
Console.WriteLine($"{(citeFencesPass ? "PASS" : "FAIL")}: Fenced rich inline nodes survive with exact spans, including unclosed and split fences");
if (!citeFencesPass) failures++;
var citeFenceWithShell = new RichDocumentParser(Registry.RichTags).Parse("~~~jsx\n" + citeFenceTag + "\n<Widget/>\n~~~\n" + shellTag);
var citeShellNodes = citeFenceWithShell.Document.Nodes.SelectMany(Walk).ToArray();
var citeShellPass = citeFenceWithShell.Document.Valid && citeFenceWithShell.Commands.Count == 1 &&
    citeFenceWithShell.Commands[0].Body == "echo HIDDEN" &&
    citeShellNodes.Count(n => n.Kind == "element" && n.Name == "Cite") == 1 &&
    citeShellNodes.Any(n => n.Kind == "unknown" && n.Name == "Widget");
Console.WriteLine($"{(citeShellPass ? "PASS" : "FAIL")}: Fenced rich inline lowering preserves root command and inert unknown wrapper");
if (!citeShellPass) failures++;

var fenceInfoSource = "~~~<text>info</text>\\ncontent\\n~~~\\n<SH>ROOT</SH>".Replace("\\n", "\n");
var fenceInfoDoc = new RichDocumentParser(Registry.RichTags).Parse(fenceInfoSource);
var fenceInfoPass = fenceInfoDoc.Document.Valid && fenceInfoDoc.Commands.Count == 1 &&
    fenceInfoDoc.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "element" && n.Name == "text");
Console.WriteLine($"{(fenceInfoPass ? "PASS" : "FAIL")}: Fence info literal survives lowering without losing root shell");
if (!fenceInfoPass) failures++;


Console.WriteLine($"{(inlineOk ? "PASS" : "FAIL")}: Rendered inline command retains one paragraph and exact spans");
if (!inlineOk) failures++;
var strongDoc = new RichDocumentParser(Registry.RichTags).Parse("**Before " + shellTag + " after**");
var strongNode = strongDoc.Document.Nodes.SingleOrDefault()?.Children?.SingleOrDefault();
var strongOk = strongDoc.Document.Valid && strongNode?.Kind == "strong" &&
    strongNode.Children?.Count == 2 && strongNode.Children[0].Text == "Before " &&
    strongNode.Children[1].Text == " after";
Console.WriteLine($"{(strongOk ? "PASS" : "FAIL")}: Rendered command preserves spanning strong emphasis");
if (!strongOk) failures++;
var listDoc = new RichDocumentParser(Registry.RichTags).Parse("- Before " + shellTag + " after");
var listParagraphs = listDoc.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "paragraph").ToArray();
var listOk = listDoc.Document.Valid && listDoc.Document.Nodes.Count == 1 &&
    listDoc.Document.Nodes[0].Kind == "list" && listParagraphs.Length == 1 &&
    listParagraphs[0].Children?.Count == 2;
Console.WriteLine($"{(listOk ? "PASS" : "FAIL")}: Rendered command retains one Markdown list item");
if (!listOk) failures++;
var codeDoc = new RichDocumentParser(Registry.RichTags).Parse("`pre " + shellTag + " post`");
var codeNodes = codeDoc.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "inlineCode").ToArray();
var codeOk = codeDoc.Document.Valid && codeNodes.Length == 1 && codeNodes[0].Text == "pre  post";
Console.WriteLine($"{(codeOk ? "PASS" : "FAIL")}: Removed command leaves intact inline code text");
if (!codeOk) failures++;
var fenceDoc = new RichDocumentParser(Registry.RichTags).Parse("~~~\npre " + shellTag + " post\n~~~");
var fenceNodes = fenceDoc.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "code").ToArray();
var fenceOk = fenceDoc.Document.Valid && fenceNodes.Length == 1 &&
    fenceNodes[0].Text?.Contains("pre  post", StringComparison.Ordinal) == true;
Console.WriteLine($"{(fenceOk ? "PASS" : "FAIL")}: Removed command leaves intact fenced code text");
if (!fenceOk) failures++;
// A command surrounded by blank lines must not leave an empty paragraph.
var blankLineDoc = new RichDocumentParser(Registry.RichTags).Parse("Before\n\n" + shellTag + "\n\nAfter");
var blankLineOk = blankLineDoc.Document.Valid && blankLineDoc.Commands.Count == 1 &&
    blankLineDoc.Document.Nodes.Count == 2 &&
    blankLineDoc.Document.Nodes.All(n => n.Kind == "paragraph" && n.Children?.Count == 1) &&
    blankLineDoc.Document.Nodes[0].Children![0].Text == "Before" &&
    blankLineDoc.Document.Nodes[1].Children![0].Text == "After";
Console.WriteLine($"{(blankLineOk ? "PASS" : "FAIL")}: Standalone command leaves no empty rendered paragraph");
if (!blankLineOk) failures++;

// Each parent declares its own complete policy for nested UI controls.
// null accepts ordinary registered UI, empty means opaque/no nested tags.
var controlsPass =
    Registry.RichTags.TryGet("box", out var boxDef) &&
    boxDef.SupportedChildren is null && boxDef.AllowAsChild &&
    Registry.RichTags.TryGet("row", out var rowDef) &&
    rowDef.SupportedChildren is null && rowDef.AllowAsChild &&
    Registry.RichTags.TryGet("SH", out var shellDef) &&
    shellDef.SupportedChildren is { Count: 0 } && !shellDef.AllowAsChild &&
    Registry.RichTags.TryGet("ATTACH", out var attachDef) &&
    attachDef.SupportedChildren is { Count: 0 } && !attachDef.AllowAsChild &&
    Registry.RichTags.TryGet("DONE", out var doneDef) &&
    doneDef.SupportedChildren is { Count: 0 } && !doneDef.AllowAsChild &&
    Registry.RichTags.TryGet("escape", out var escapeDef) &&
    escapeDef.SupportedChildren is { Count: 0 } && escapeDef.AllowAsChild &&
    Registry.RichTags.TryGet("WritingBlock", out var writingDef) &&
    writingDef.SupportedChildren is { Count: 0 } && writingDef.AllowAsChild;
Console.WriteLine($"{(controlsPass ? "PASS" : "FAIL")}: Declarative parent-supported children and opaque boundaries");
if (!controlsPass) failures++;

// Architectural regression: command and escape semantics must be registered
// callbacks, not ad-hoc command-role branches in RichDocumentParser.
var dispatcherContract =
    new[] { "SH", "ATTACH", "DONE", "escape" }
        .All(name => Registry.RichTags.TryGet(name, out var definition) &&
            definition.ProcessContent is not null) &&
    Registry.RichTags.TryGet("box", out var displayDefinition) &&
    displayDefinition.ProcessContent is null;
Console.WriteLine($"{(dispatcherContract ? "PASS" : "FAIL")}: Command and escape handlers are registry-driven");
if (!dispatcherContract) failures++;

// The reusable Core catalog has UI only; the Agent extends it from its own
// command definitions without changing the Core parser or authorizing a shell.
var separatedCatalogs =
    !RichTagRegistry.Ui.TryGet("SH", out _) &&
    !RichTagRegistry.Ui.TryGet("ATTACH", out _) &&
    !RichTagRegistry.Ui.TryGet("DONE", out _) &&
    Registry.All.All(command => Registry.RichTags.TryGet(command.Name, out var tag) &&
        tag.ProcessContent is not null &&
        !tag.AllowAsChild) &&
    new RichDocumentParser().Parse("<SH>echo inert</SH>").Commands.Count == 0;
Console.WriteLine($"{(separatedCatalogs ? "PASS" : "FAIL")}: Agent extends Core UI with executable tags only in Agent context");
if (!separatedCatalogs) failures++;

// SVG is one opaque raw-editor definition. Its geometry and value rules
// belong exclusively to the trusted frontend component.
var svgPolicies =
    RichTagRegistry.Ui.TryGet("svg", out var svgRoot) &&
    svgRoot.RequiredAncestors is null && svgRoot.Role == RichTagRole.RawEditor &&
    svgRoot.SupportedChildren is { Count: 0 } &&
    svgRoot.AllowedAttributes is { } svgAttributes &&
    svgAttributes.SetEquals(["viewBox", "width", "height", "preserveAspectRatio", "role", "aria-label"]) &&
    !RichTagRegistry.Ui.TryGet("g", out _) &&
    !RichTagRegistry.Ui.TryGet("path", out _) &&
    !RichTagRegistry.Ui.TryGet("rect", out _) &&
    RichTagRegistry.Ui.TryGet("text", out var regularText) &&
    regularText.Name == "text";
Console.WriteLine($"{(svgPolicies ? "PASS" : "FAIL")}: SVG is an opaque registered control");
if (!svgPolicies) failures++;

// A new UI control's nesting/inline behavior comes entirely from registered
// metadata; the generic parser has no name allowlist to update.
var customUiCatalog = RichTagRegistry.Ui.With([
    new RichTagDefinition("custom-inline", RichTagRole.Container, null,
        SupportedChildren: null, AllowAsChild: true) { Inline = true }
]);
var customUi = new RichDocumentParser(customUiCatalog)
    .Parse("before <custom-inline>hello</custom-inline> after");
var customInlinePass = customUi.Document.Valid && customUi.Commands.Count == 0 &&
    customUi.Document.Nodes.SelectMany(Walk).Any(n =>
        n.Kind == "element" && n.Name == "custom-inline" &&
        n.Children?.Any(child => child.Text?.Contains("hello", StringComparison.Ordinal) == true) == true);
Console.WriteLine($"{(customInlinePass ? "PASS" : "FAIL")}: Registered inline control requires no parser special case");
if (!customInlinePass) failures++;

// An ancestor-constrained inline tag must retain its registered phrasing metadata
// during normalization, even though it is not root-eligible.
var contextualInlineCatalog = RichTagRegistry.Ui.With([
    new RichTagDefinition("parent-inline", RichTagRole.Container, null,
        new HashSet<string>(["nested-phrase"], StringComparer.Ordinal), AllowAsChild: true)
        { Inline = true },
    new RichTagDefinition("nested-phrase", RichTagRole.Container, null,
        SupportedChildren: null, AllowAsChild: true)
        { RequiredAncestors = new HashSet<string>(["parent-inline"], StringComparer.Ordinal),
            Inline = true }
]);
var contextualInline = new RichDocumentParser(contextualInlineCatalog).Parse(
    "<parent-inline>before <nested-phrase>inside</nested-phrase> after</parent-inline>");
var contextualInlinePass = contextualInline.Document.Valid &&
    contextualInline.Document.Nodes.SelectMany(Walk).Any(n =>
        n.Kind == "element" && n.Name == "nested-phrase" &&
        n.Children?.SelectMany(Walk).Any(c => c.Text == "inside") == true);
Console.WriteLine($"{(contextualInlinePass ? "PASS" : "FAIL")}: Ancestor-restricted inline metadata survives normalization");
if (!contextualInlinePass) failures++;

// Raw SVG content travels as data, not executable Agent sub-tags.
var rawSvgSource = "<svg viewBox=\"0 0 80 80\"><text>hello</text><SH>INERT</SH></svg><SH>ROOT</SH>";
var rawSvgDocument = new RichDocumentParser(Registry.RichTags).Parse(rawSvgSource);
var opaqueSvgPass = rawSvgDocument.Document.Valid &&
    rawSvgDocument.Commands.Select(c => c.Body).SequenceEqual(["ROOT"]) &&
    rawSvgDocument.Document.Nodes.SelectMany(Walk).Any(n =>
        n.Kind == "raw" && n.Name == "svg" &&
        n.Text == "<text>hello</text><SH>INERT</SH>" &&
        n.Attributes?["viewBox"] == "0 0 80 80");
Console.WriteLine($"{(opaqueSvgPass ? "PASS" : "FAIL")}: SVG transport and Agent command isolation");
if (!opaqueSvgPass) failures++;

// An arbitrary registered parent (not only SVG) owns the child allowlist.
// Unknown/disallowed wrappers must never promote contained host commands.
var constrainedCatalog = Registry.RichTags.With([
    new RichTagDefinition("restricted", RichTagRole.Container, null,
        new HashSet<string>(["allowed"], StringComparer.Ordinal), AllowAsChild: true),
    new RichTagDefinition("allowed", RichTagRole.Container, null,
        SupportedChildren: null, AllowAsChild: true)
        { RequiredAncestors = new HashSet<string>(["restricted"], StringComparer.Ordinal) }
]);
var allowedChild = new RichDocumentParser(constrainedCatalog).Parse(
    "<restricted><allowed>yes</allowed><SH>INERT</SH></restricted><SH>ROOT</SH>");
var allowedChildPass = allowedChild.Document.Valid &&
    allowedChild.Commands.Select(c => c.Body).SequenceEqual(["ROOT"]) &&
    allowedChild.Document.Nodes.SelectMany(Walk).Any(n => n.Name == "allowed" &&
        n.Children?.SelectMany(Walk).Any(c => c.Text == "yes") == true);
Console.WriteLine($"{(allowedChildPass ? "PASS" : "FAIL")}: Explicit parent child names admit registered ancestry-restricted tags");
if (!allowedChildPass) failures++;

var deniedChild = new RichDocumentParser(constrainedCatalog).Parse(
    "<restricted><box><SH>INERT</SH></box></restricted><SH>ROOT</SH>");
var deniedChildPass = deniedChild.Document.Valid &&
    deniedChild.Commands.Select(c => c.Body).SequenceEqual(["ROOT"]) &&
    deniedChild.Document.Nodes.Any(n => n.Kind == "unknown" && n.Name == "restricted");
Console.WriteLine($"{(deniedChildPass ? "PASS" : "FAIL")}: Disallowed children are inert and cannot promote nested commands");
if (!deniedChildPass) failures++;

var unknownRootOnly = new RichDocumentParser(Registry.RichTags).Parse(
    "<UnknownControl><SH>INERT</SH><box><ATTACH>INERT.txt</ATTACH></box></UnknownControl><SH>ROOT</SH>");
var unknownRootOnlyPass = unknownRootOnly.Document.Valid &&
    unknownRootOnly.Commands.Select(c => c.Body).SequenceEqual(["ROOT"]) &&
    unknownRootOnly.Document.Nodes.Any(n => n.Kind == "unknown" && n.Name == "UnknownControl");
Console.WriteLine($"{(unknownRootOnlyPass ? "PASS" : "FAIL")}: Unknown-wrapper children never gain document-root command authority");
if (!unknownRootOnlyPass) failures++;

var nestedOnlyAtRoot = new RichDocumentParser(constrainedCatalog).Parse(
    "<allowed>not-root</allowed><SH>ROOT</SH>");
var nestedOnlyAtRootPass = nestedOnlyAtRoot.Document.Valid &&
    nestedOnlyAtRoot.Commands.Select(c => c.Body).SequenceEqual(["ROOT"]) &&
    nestedOnlyAtRoot.Document.Nodes.Any(n => n.Kind == "unknown" && n.Name == "allowed");
Console.WriteLine($"{(nestedOnlyAtRootPass ? "PASS" : "FAIL")}: Ancestor-restricted definitions cannot render at document root");
if (!nestedOnlyAtRootPass) failures++;

// Context eligibility uses any matching structural ancestor, including
// ancestors above a normal or unsupported intermediate wrapper.
var ancestorsCatalog = Registry.RichTags.With([
    new RichTagDefinition("ancestor-a", RichTagRole.Container, null, null, true),
    new RichTagDefinition("ancestor-b", RichTagRole.Container, null, null, true),
    new RichTagDefinition("ancestor-dependent", RichTagRole.Container, null, null, true)
    {
        RequiredAncestors = new HashSet<string>(["ancestor-a", "ancestor-b"], StringComparer.Ordinal)
    }
]);
var ancestry = new RichDocumentParser(ancestorsCatalog).Parse(
    "<ancestor-a><box><ancestor-dependent><text>nested</text></ancestor-dependent></box></ancestor-a>" +
    "<ancestor-b><Widget><ancestor-dependent>also nested</ancestor-dependent></Widget></ancestor-b>" +
    "<box><ancestor-dependent>missing ancestor</ancestor-dependent></box>" +
    "<ancestor-dependent><SH>INERT</SH></ancestor-dependent><SH>ROOT</SH>");
var ancestryNodes = ancestry.Document.Nodes.SelectMany(Walk).ToArray();
var ancestryPass = ancestry.Document.Valid &&
    ancestry.Commands.Select(c => c.Body).SequenceEqual(["ROOT"]) &&
    ancestryNodes.Count(n => n.Kind == "element" && n.Name == "ancestor-dependent") == 2 &&
    ancestryNodes.Count(n => n.Kind == "unknown" && n.Name == "ancestor-dependent") == 2 &&
    ancestryNodes.Any(n => n.Kind == "element" && n.Name == "ancestor-dependent" &&
        n.Children?.SelectMany(Walk).Any(child => child.Kind == "element" && child.Name == "text") == true);
Console.WriteLine($"{(ancestryPass ? "PASS" : "FAIL")}: Any required ancestor, transitive nesting, and inert rejected wrappers");
if (!ancestryPass) failures++;

// An incomplete ancestor must not grant admission after structural reparenting.
var incompleteAncestry = new RichDocumentParser(ancestorsCatalog).Parse(
    "<ancestor-a><ancestor-dependent><SH>INERT</SH></ancestor-dependent><SH>ROOT</SH>");
var incompleteAncestryPass = incompleteAncestry.Document.Valid &&
    incompleteAncestry.Commands.Select(c => c.Body).SequenceEqual(["ROOT"]) &&
    incompleteAncestry.Document.Nodes.Any(n =>
        n.Kind == "unknown" && n.Name == "ancestor-dependent");
Console.WriteLine($"{(incompleteAncestryPass ? "PASS" : "FAIL")}: Incomplete parent cannot satisfy required ancestors");
if (!incompleteAncestryPass) failures++;

var duplicateNameRejected = false;
try
{
    _ = RichTagRegistry.Ui.With([
        new RichTagDefinition("text", RichTagRole.RawEditor, null,
            RichTagDefinition.NoChildren, true)
    ]);
}
catch (ArgumentException) { duplicateNameRejected = true; }
Console.WriteLine($"{(duplicateNameRejected ? "PASS" : "FAIL")}: Same-name definitions are forbidden");
if (!duplicateNameRejected) failures++;

var unknownRequiredAncestorRejected = false;
try
{
    _ = RichTagRegistry.Ui.With([
        new RichTagDefinition("orphan-child", RichTagRole.Container, null, null, true)
        {
            RequiredAncestors = new HashSet<string>(["missing-ancestor"], StringComparer.Ordinal)
        }
    ]);
}
catch (ArgumentException) { unknownRequiredAncestorRejected = true; }
Console.WriteLine($"{(unknownRequiredAncestorRejected ? "PASS" : "FAIL")}: Required ancestor references must be registered");
if (!unknownRequiredAncestorRejected) failures++;

var emptyRequiredAncestorsRejected = false;
try
{
    _ = RichTagRegistry.Ui.With([
        new RichTagDefinition("never-allowed", RichTagRole.Container, null, null, true)
        {
            RequiredAncestors = new HashSet<string>(StringComparer.Ordinal)
        }
    ]);
}
catch (ArgumentException) { emptyRequiredAncestorsRejected = true; }
Console.WriteLine($"{(emptyRequiredAncestorsRejected ? "PASS" : "FAIL")}: Empty required ancestor sets are invalid");
if (!emptyRequiredAncestorsRejected) failures++;

var invalidChildRegistrationRejected = false;
try
{
    _ = RichTagRegistry.Ui.With([
        new RichTagDefinition("broken-children", RichTagRole.Container, null,
            new HashSet<string>(["missing-tag"], StringComparer.Ordinal), AllowAsChild: true)
    ]);
}
catch (ArgumentException)
{
    invalidChildRegistrationRejected = true;
}
Console.WriteLine($"{(invalidChildRegistrationRejected ? "PASS" : "FAIL")}: Unknown child IDs fail during catalog composition");
if (!invalidChildRegistrationRejected) failures++;


// A model can literally author the former private-use marker sequence.
// The restored inline node must stay at its real UTF-16 position.
var collisionSource = "pre " + (char)0xE000 + (char)0xE001 + " <text strong>node</text> post";
var collisionResult = new RichDocumentParser(Registry.RichTags).Parse(collisionSource);
var collisionChildren = collisionResult.Document.Nodes.FirstOrDefault()?.Children;
var collisionStart = collisionSource.IndexOf("<text strong>", StringComparison.Ordinal);
var collisionElement = collisionChildren?.FirstOrDefault(n => n.Name == "text");
var collisionPass = collisionResult.Document.Valid && collisionResult.Commands.Count == 0 &&
    collisionChildren?.Count == 3 &&
    collisionChildren[0].Text == ("pre " + (char)0xE000 + (char)0xE001 + " ") &&
    collisionElement is not null && collisionElement.SourceSpan.Start == collisionStart &&
    collisionElement.SourceSpan.End == collisionSource.IndexOf("</text>", StringComparison.Ordinal) + 7 &&
    collisionElement.Id == $"{collisionStart}:element" &&
    collisionChildren[2].Text == " post" &&
    collisionChildren[0].SourceSpan.End == collisionStart &&
    collisionChildren[2].SourceSpan.Start == collisionElement.SourceSpan.End;
Console.WriteLine($"{(collisionPass ? "PASS" : "FAIL")}: Model-authored private-use marker collision preserves exact text, order, node IDs, UTF-16 spans");
if (!collisionPass) failures++;

// GFM task checkboxes are valid native phrasing inputs inside Markdown list
// paragraphs, but remain forbidden inside interactive rich links/buttons.
var taskSource = "- [x] Completed\n- [ ] Pending";
var taskResult = new RichDocumentParser(Registry.RichTags).Parse(taskSource);
var taskNodes = taskResult.Document.Nodes.SelectMany(Walk)
    .Where(node => node.Kind == "taskCheckbox").ToArray();
var taskPass = taskResult.Document.Valid && taskResult.Commands.Count == 0 &&
    taskNodes.Length == 2 && taskNodes[0].Attributes?.GetValueOrDefault("checked") == "true" &&
    taskNodes[1].Attributes?.GetValueOrDefault("checked") == "false";
Console.WriteLine($"{(taskPass ? "PASS" : "FAIL")}: GFM task checkboxes remain accessible inside Markdown paragraphs");
if (!taskPass) failures++;

// Screenshot regression: indentation of nested UI is presentation whitespace,
// never implicit Markdown code. The root Markdown pipeline retains indented code.
var capturedLayout = File.ReadAllText(Path.Combine("tests", "fixtures",
    "rich-model-ui", "indented-layout-capture.md"));
var capturedDocument = new RichDocumentParser(Registry.RichTags).Parse(capturedLayout);
var captureNodes = capturedDocument.Document.Nodes.SelectMany(Walk).ToArray();
var capturedPass = capturedDocument.Document.Valid && capturedDocument.Commands.Count == 0 &&
    captureNodes.Count(n => n.Kind == "code") == 0 &&
    captureNodes.Count(n => n.Kind == "strong") >= 2 &&
    captureNodes.Count(n => n.Kind == "element" && n.Name == "box") >= 4 &&
    captureNodes.Any(n => n.Kind == "element" && n.Name == "row") &&
    captureNodes.Any(n => n.Kind == "element" && n.Name == "grid") &&
    captureNodes.Count(n => n.Kind == "element" && n.Name == "grid-item") == 2 &&
    captureNodes.Any(n => n.Kind == "element" && n.Name == "icon") &&
    captureNodes.Any(n => n.Kind == "element" && n.Name == "text" &&
        n.Children?.Any(t => t.Text?.Contains("Identify known tags") == true) == true);
Console.WriteLine($"{(capturedPass ? "PASS" : "FAIL")}: Captured indented nested UI is rich layout, not code; nodes={captureNodes.Length}; code={captureNodes.Count(n => n.Kind == "code")}");
if (!capturedPass) failures++;
var rootIndented = new RichDocumentParser(Registry.RichTags).Parse("Intro\\n\\n    actual code".Replace("\\n", "\n"));
var rootIndentedPass = rootIndented.Document.Valid &&
    rootIndented.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "code");
Console.WriteLine($"{(rootIndentedPass ? "PASS" : "FAIL")}: Root Markdown retains four-space code blocks");
if (!rootIndentedPass) failures++;
var containerFenced = new RichDocumentParser(Registry.RichTags).Parse("<box>\\n  ~~~\\n  code inside rich box\\n  ~~~\\n</box>".Replace("\\n", "\n"));
var containerFencedPass = containerFenced.Document.Valid &&
    containerFenced.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "code" &&
        n.Text?.Contains("code inside rich box") == true);
Console.WriteLine($"{(containerFencedPass ? "PASS" : "FAIL")}: Explicit fenced code remains available inside rich layout");
if (!containerFencedPass) failures++;

var fixture = File.ReadAllText(Path.Combine("tests", "fixtures", "rich-model-ui", "custom-controls-question.md"));
var parsedQuestion = new RichDocumentParser(Registry.RichTags).Parse(fixture);
var question = parsedQuestion.Document.Nodes.SingleOrDefault(n => n.Kind == "question");
var choices = question?.Children?.Where(n => n.Kind == "questionOption").ToArray() ?? [];
var buttons = question?.Children?.Where(n => n.Kind == "questionButton").ToArray() ?? [];
var radioPass = parsedQuestion.Document.Valid && parsedQuestion.Commands.Count == 0 &&
    question?.Attributes?["initial"] == "A" &&
    choices.Length == 4 && buttons.Length == 1 &&
    buttons[0].Children is { Count: > 0 } buttonChildren &&
    buttonChildren.All(child => child.Kind is not ("paragraph" or "heading" or "list" or "taskCheckbox")) &&
    string.Join("", choices.Select(n => n.Attributes!["value"])) == "ABCD" &&
    choices.All(n => n.Attributes!["message"].StartsWith("Grill Me Q1: ", StringComparison.Ordinal)) &&
    choices[0].Attributes!["message"].Contains("minimum interval between request starts") &&
    choices[1].Attributes!["message"].Contains("cooldown after each completed request") &&
    choices[2].Attributes!["message"].Contains("delay only after rate limiting") &&
    choices[3].Attributes!["message"].Contains("different semantics");
Console.WriteLine($"{(radioPass ? "PASS" : "FAIL")}: Real radio fixture; valid={parsedQuestion.Document.Valid}; choices={choices.Length}; diagnostics={string.Join(",", parsedQuestion.Document.Diagnostics.Select(d => d.Code))}");
if (!radioPass) failures++;
var wrappedFixture = File.ReadAllText(Path.Combine("tests", "fixtures", "rich-model-ui", "wrapped-question.md"));
var wrappedResult = new RichDocumentParser(Registry.RichTags).Parse(wrappedFixture);
var wrappedQuestion = wrappedResult.Document.Nodes.SingleOrDefault(n => n.Kind == "question");
var wrappedPass = wrappedResult.Document.Valid && wrappedResult.Commands.Count == 0 &&
    wrappedQuestion?.Attributes?.GetValueOrDefault("initial") == "A" &&
    wrappedQuestion.Attributes.GetValueOrDefault("box") == "true" &&
    wrappedQuestion.Attributes.GetValueOrDefault("box.radius") == "xl" &&
    wrappedQuestion.Children?.Count(n => n.Kind == "questionOption") == 2 &&
    wrappedQuestion.Children.Any(n => n.Kind == "questionOption" &&
        n.Attributes?.GetValueOrDefault("message") == "Choice: A");
Console.WriteLine($"{(wrappedPass ? "PASS" : "FAIL")}: Original boxed radio fixture; valid={wrappedResult.Document.Valid}; diagnostics={string.Join("|", wrappedResult.Document.Diagnostics.Select(d => d.Code))}");
if (!wrappedPass) failures++;

// Closing-tag-looking code within radio labels/buttons is never structural.
var radioCode = wrappedFixture
    .Replace("**A — Send to the collector.** One pipeline, one endpoint.",
        "Literal code: `</radio>` is a label example.")
    .Replace("Confirm</button>", "Confirm `</button>` in code</button>");
var radioCodeResult = new RichDocumentParser(Registry.RichTags).Parse(radioCode);
var radioCodeNode = radioCodeResult.Document.Nodes.SingleOrDefault(n => n.Kind == "question");
var radioCodeOption = radioCodeNode?.Children?.FirstOrDefault(n => n.Kind == "questionOption");
var radioCodeButton = radioCodeNode?.Children?.FirstOrDefault(n => n.Kind == "questionButton");
var radioCodePass = radioCodeResult.Document.Valid && radioCodeResult.Commands.Count == 0 &&
    radioCodeOption?.Children?.SelectMany(Walk).Any(n => n.Kind == "inlineCode" && n.Text == "</radio>") == true &&
    radioCodeButton?.Children?.SelectMany(Walk).Any(n => n.Kind == "inlineCode" && n.Text == "</button>") == true &&
    radioCodeOption.SourceSpan.End == radioCode.IndexOf("    </radio>", StringComparison.Ordinal) + 12;
Console.WriteLine($"{(radioCodePass ? "PASS" : "FAIL")}: Radio and button delimiters inside inline Markdown code remain inert with original spans");
if (!radioCodePass) failures++;

var rejectedBox = new RichDocumentParser(Registry.RichTags).Parse(wrappedFixture.Replace("<box border", "<box onClick=\"unsafe()\" border"));
var rejectedBoxPass = rejectedBox.Document.Valid && rejectedBox.Commands.Count == 0 &&
    rejectedBox.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "unknown" && n.Name == "box");
Console.WriteLine($"{(rejectedBoxPass ? "PASS" : "FAIL")}: Unsafe radio box wrapper remains inert");
if (!rejectedBoxPass) failures++;

var staticStyle = new RichDocumentParser(Registry.RichTags).Parse("<text textAlign=\"center\" tabularNums>42</text><title textAlign=\"center\" tabularNums>Title</title>");
var staticStylePass = staticStyle.Document.Valid &&
    staticStyle.Document.Nodes.SelectMany(Walk).Count(n => n.Kind == "element" &&
        n.Name is "text" or "title" &&
        n.Attributes?.GetValueOrDefault("textAlign") == "center" &&
        n.Attributes.GetValueOrDefault("tabularNums") == "true") == 2;
Console.WriteLine($"{(staticStylePass ? "PASS" : "FAIL")}: Previously supported text/title static attributes");
if (!staticStylePass) failures++;

// Inline rich elements must not fragment their containing Markdown block.
// Recursion still validates each element and its children in C#.
var inlineTests = new (string Name, string Source, Func<RichParseResult, bool> Check)[]
{
    ("Mixed paragraph", "Before <text strong>inside</text> after", r =>
        r.Document.Valid && r.Commands.Count == 0 &&
        r.Document.Nodes.Count == 1 && r.Document.Nodes[0].Kind == "paragraph" &&
        r.Document.Nodes[0].Children?.Count == 3 &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(0)?.Text == "Before " &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(1)?.Name == "text" &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(1)?.Attributes?.GetValueOrDefault("strong") == "true" &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(1)?.Children?.Count == 1 &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(1)?.Children?.ElementAtOrDefault(0)?.Kind == "text" &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(1)?.Children?.ElementAtOrDefault(0)?.Text == "inside" &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(2)?.Text == " after"),
    ("Emphasis spanning an inline rich element", "**Before <text strong>inside</text> after**", r =>
        r.Document.Valid && r.Document.Nodes.Count == 1 &&
        r.Document.Nodes[0].Kind == "paragraph" &&
        r.Document.Nodes[0].Children?.Count == 1 &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(0)?.Kind == "strong" &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(0)?.Children?.Any(c => c.Name == "text") == true),
    ("Rich element inside list item", "- Before <text strong>inside</text> after", r =>
        r.Document.Valid && r.Document.Nodes.Count == 1 &&
        r.Document.Nodes[0].Kind == "list" &&
        r.Document.Nodes[0].Children?.Count == 1 &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(0)?.Children?.Any(n =>
            n.Kind == "paragraph" && n.Children?.Any(c => c.Name == "text") == true) == true),
    ("Recursive box with inline rich child", "<box>hello <text strong>world</text></box>", r =>
        r.Document.Valid && r.Document.Nodes.Count == 1 &&
        r.Document.Nodes[0].Name == "box" &&
        r.Document.Nodes[0].Children?.Count == 1 &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(0)?.Kind == "paragraph" &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(0)?.Children?.Any(n => n.Name == "text" &&
            n.Children?.Count == 1 && n.Children?.ElementAtOrDefault(0)?.Text == "world") == true),
    ("Inline rich tag inside Markdown code remains an element", "`pre <text strong>world</text> post`", r =>
        r.Document.Valid && r.Commands.Count == 0 &&
        r.Document.Nodes.SelectMany(Walk).Any(n => n.Name == "text" &&
            n.Children?.Any(c => c.Text == "world") == true) &&
        r.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "inlineCode" && n.Text == "pre ") &&
        r.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "inlineCode" && n.Text == " post")),
    ("Fenced Markdown rich tag does not discard later SH", "~~~\n<text>world</text>\n~~~\n<SH>echo OK</SH>", r =>
        r.Document.Valid && r.Commands.Select(c => c.Name).SequenceEqual(["SH"]) &&
        r.Document.Nodes.SelectMany(Walk).Any(n => n.Name == "text" &&
            n.Children?.Any(c => c.Text == "world") == true)),
    ("Standalone inline element has no paragraph inside span", "<text strong>world</text>", r =>
        r.Document.Valid && r.Document.Nodes.Count == 1 &&
        r.Document.Nodes[0].Kind == "paragraph" &&
        r.Document.Nodes[0].Children?.Count == 1 &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(0)?.Name == "text" &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(0)?.Children?.Count == 1 &&
        r.Document.Nodes[0].Children?.ElementAtOrDefault(0)?.Children?.ElementAtOrDefault(0)?.Kind == "text"),
};
foreach (var (name, source, verify) in inlineTests)
{
    var parsed = new RichDocumentParser(Registry.RichTags).Parse(source);
    var pass = verify(parsed);
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {name}; valid={parsed.Document.Valid}; errors={string.Join(",", parsed.Document.Diagnostics.Select(d => d.Code))}");
    if (!pass) failures++;
}

var svgShellAttachSource = "<svg>before<SH>echo HIDDEN</SH>middle<ATTACH>path.txt</ATTACH>after<circle r='2'/></svg>";
var svgWrappedShellSource = "<svg><widget>before<SH>echo HIDDEN</SH>after</widget></svg>";
var svgTests = new (string Name, string Source, bool Valid, Func<RichParseResult, bool> Extra)[]
{
    ("Representative SVG", File.ReadAllText(Path.Combine("tests","fixtures","rich-model-ui","representative-svg.md")), true,
        r => r.Document.Nodes.Any(n => n.Kind == "raw" && n.Name == "svg" &&
            n.Text?.Contains("<g>", StringComparison.Ordinal) == true &&
            n.Text?.Contains("Collector", StringComparison.Ordinal) == true)),
    ("SVG embedded SH is inert", "<svg viewBox=\"0 0 100 100\"><SH>echo YES</SH></svg>", true, r => r.Commands.Count == 0),
    ("SVG keeps nested SH and ATTACH inert", svgShellAttachSource, true, r =>
        r.Commands.Count == 0 && r.Document.Nodes.Single().Kind == "raw" &&
        r.Document.Nodes.Single().Text == "before<SH>echo HIDDEN</SH>middle<ATTACH>path.txt</ATTACH>after<circle r='2'/>"),
    ("SVG unknown wrapper keeps SH inert", svgWrappedShellSource, true, r =>
        r.Commands.Count == 0 && r.Document.Nodes.Single().Kind == "raw"),
    ("SVG root event attribute rejected", "<svg onload=\"alert(1)\"></svg>", true,
        r => r.Commands.Count == 0 && r.Document.Nodes.Any(n => n.Kind == "unknown")),
    ("SVG unsafe child paint stays opaque for TSX to reject", "<svg><rect fill=\"url(#evil)\" /></svg>", true,
        r => r.Commands.Count == 0 && r.Document.Nodes.Any(n => n.Kind == "raw")),
    ("SVG text preserved unchanged", "<svg viewBox=\"0 0 80 80\"><text x=\"15\" y=\"30\">Hello</text></svg>", true,
        r => r.Document.Nodes.Any(n => n.Kind == "raw" && n.Text?.Contains("<text x=", StringComparison.Ordinal) == true)),
    ("SVG nested foreign UI is inert", "<svg><box>bad</box><SH>INERT</SH></svg>", true, r => r.Commands.Count == 0),
    ("SVG foreign UI under unknown wrapper inert", "<svg><widget><box>bad</box></widget></svg>", true, r => r.Commands.Count == 0),
    ("SVG unknown wrapper deferred to TSX", "<svg><widget>literal</widget></svg>", true,
        r => r.Document.Nodes.Any(n => n.Kind == "raw" && n.Name == "svg")),
    ("SVG malformed wrapper keeps SH inert", "<svg><widget><SH>echo YES</SH></widget></svg>", true,
        r => r.Commands.Count == 0 && r.Document.Nodes.Any(n => n.Kind == "raw")),
    ("Nested SVG remains opaque for TSX to reject", "<svg><svg><circle r=\"2\"/></svg></svg>", true, r => r.Commands.Count == 0),
    ("SVG geometry bounds deferred to TSX", "<svg><circle r=\"100000000\" /></svg>", true, r => r.Commands.Count == 0),
    ("SVG false closer in quoted child attribute cannot expose SH", "<svg><text title=\"</svg>\"><SH>INERT</SH></text></svg><SH>ROOT</SH>", true,
        r => r.Commands.Select(c => c.Body).SequenceEqual(["ROOT"])),
    ("SVG false closer in comment cannot expose SH", "<svg><!-- </svg> --><SH>INERT</SH></svg><SH>ROOT</SH>", true,
        r => r.Commands.Select(c => c.Body).SequenceEqual(["ROOT"])),
    ("Nested same-name SVG boundary cannot expose SH", "<svg><svg></svg><SH>INERT</SH></svg><SH>ROOT</SH>", true,
        r => r.Commands.Select(c => c.Body).SequenceEqual(["ROOT"])),
};
foreach (var (name, source, valid, extra) in svgTests)
{
    var result = new RichDocumentParser(Registry.RichTags).Parse(source);
    var pass = result.Document.Valid == valid && extra(result);
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {name}; valid={result.Document.Valid}; errors={string.Join(",", result.Document.Diagnostics.Select(d => d.Code))}");
    if (!pass) failures++;
}
// Position-independent matching includes markup-looking tags within Markdown.
var nestedSource = "<box><box><text>Nested sibling</text></box> outer</box>";
var nestedResult = new RichDocumentParser(Registry.RichTags).Parse(nestedSource);
var nestedPass = nestedResult.Document.Valid &&
    nestedResult.Document.Nodes.SingleOrDefault()?.Name == "box" &&
    nestedResult.Document.Nodes[0].Children?.Any(n => n.Name == "box") == true;
Console.WriteLine($"{(nestedPass ? "PASS" : "FAIL")}: Nested UI delimiters are balanced");
if (!nestedPass) failures++;

// Adversarial raw-editor siblings: reparsing the full remaining suffix
// for each opener used to grow quadratically before opaque scope isolation.
var rawSiblings = string.Concat(Enumerable.Range(0, 1500)
    .Select(i => $"<WritingBlock variant=\"document\">\\nDraft {i}\\n</WritingBlock>\\n")).Replace("\\n", "\n");
var rawTimer = System.Diagnostics.Stopwatch.StartNew();
GC.Collect();
var rawBefore = GC.GetAllocatedBytesForCurrentThread();
var rawResult = new RichDocumentParser(Registry.RichTags).Parse(rawSiblings);
rawTimer.Stop();
var rawBytes = GC.GetAllocatedBytesForCurrentThread() - rawBefore;
var rawCount = rawResult.Document.Nodes.SelectMany(Walk).Count(n => n.Name == "WritingBlock");
var rawPass = rawResult.Document.Valid && rawResult.Commands.Count == 0 && rawCount == 1500 &&
    rawTimer.Elapsed < TimeSpan.FromSeconds(8) && rawBytes < 128L * 1024 * 1024;
Console.WriteLine($"{(rawPass ? "PASS" : "FAIL")}: 1500 WritingBlock siblings in {rawTimer.ElapsedMilliseconds} ms, {rawBytes / 1024:N0} KiB, raw nodes={rawCount}; diagnostics={string.Join("|", rawResult.Document.Diagnostics.Select(d => d.Code))}");
if (!rawPass) failures++;

var wideSource = string.Concat(Enumerable.Range(0, 900)
    .Select(i => $"<box><text>Label {i}</text></box>\\n")).Replace("\\n", "\n");
var timer = System.Diagnostics.Stopwatch.StartNew();
var wideParsed = new RichDocumentParser(Registry.RichTags).Parse(wideSource);
timer.Stop();
var widePass = wideParsed.Document.Valid && wideParsed.Commands.Count == 0 &&
    wideParsed.Document.Nodes.Count(n => n.Name == "box") == 900 &&
    timer.Elapsed < TimeSpan.FromSeconds(8);
Console.WriteLine($"{(widePass ? "PASS" : "FAIL")}: 900 sibling containers bounded in {timer.ElapsedMilliseconds} ms");
if (!widePass) failures++;

var deepSource = string.Concat(Enumerable.Repeat("<box>\n", 20)) +
    "~~~\n<SH>\necho NEVER\n</SH>\n~~~\n" +
    string.Concat(Enumerable.Repeat("</box>\n", 20));
timer.Restart();
var deepParsed = new RichDocumentParser(Registry.RichTags).Parse(deepSource);
timer.Stop();
var deepPass = deepParsed.Document.Valid && deepParsed.Commands.Count == 0 &&
    timer.Elapsed < TimeSpan.FromSeconds(8);
Console.WriteLine($"{(deepPass ? "PASS" : "FAIL")}: Nested protected code bounded in {timer.ElapsedMilliseconds} ms");
if (!deepPass) failures++;

var alternatingSource = string.Concat(Enumerable.Range(0, 600)
    .Select(i => $"Part {i} <text strong>Value {i}</text> "));
GC.Collect();
var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
timer.Restart();
var alternatingParsed = new RichDocumentParser(Registry.RichTags).Parse(alternatingSource);
timer.Stop();
var bytesAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
var inlineNodes = alternatingParsed.Document.Nodes.SelectMany(Walk).Count(n => n.Name == "text");
var alternatingPass = alternatingParsed.Document.Valid && alternatingParsed.Commands.Count == 0 &&
    inlineNodes == 600 && timer.Elapsed < TimeSpan.FromSeconds(8) &&
    bytesAllocated < 128L * 1024 * 1024;
Console.WriteLine($"{(alternatingPass ? "PASS" : "FAIL")}: 600 alternating rich/Markdown inlines in {timer.ElapsedMilliseconds} ms, {bytesAllocated / 1024:N0} KiB allocated; rich nodes={inlineNodes}");
if (!alternatingPass) failures++;


// Regressions for expected invalid model syntax: record only parser-originated
// first-chance exceptions, not incidental third-party framework exceptions.
var exceptionCases = new (string Name, string Source, bool Valid, string? Diagnostic,
    string? Unknown, string[] Commands)[]
{
    ("Dynamic JSX literal", "<box border={{size:alert(1)}}>unsafe</box><SH>ROOT</SH>",
        true, null, "box", ["SH"]),
    ("Duplicate object key", "<box border={{size:1,size:2}}>x</box>",
        true, null, "box", []),
    ("Invalid JSON escape", "<box border={{label:\"\\q\"}}>x</box>",
        true, null, "box", []),
    ("Invalid object separator", "<box border={{size:1 color:\"red\"}}>x</box>",
        true, null, "box", []),
    ("Trailing array comma", "<box border={{sizes:[1,2,]}}>x</box>",
        true, null, "box", []),
    ("Object-only expression", "<box border={[1,2]}>x</box>",
        true, null, "box", []),
    ("Unsafe DOM attribute", "<box onClick=\"run()\"><SH>INERT</SH></box><SH>ROOT</SH>",
        true, null, "box", ["SH"]),
    ("Duplicate attribute", "<box gap={1} gap={2}>x</box>",
        true, null, "box", []),
    ("Unknown SVG child", "<svg><widget><SH>INERT</SH></widget></svg><SH>ROOT</SH>",
        true, null, null, ["SH"]),
    ("Unsafe SVG paint", "<svg><rect fill=\"url(#evil)\" /></svg><SH>ROOT</SH>",
        true, null, null, ["SH"]),
    ("Nested SVG", "<svg><svg><circle r=\"2\"/></svg></svg>",
        true, null, null, []),
    ("Unsafe command attribute", "<SH onClick=\"bad\">ROOT</SH>",
        false, "Unsupported or duplicate SH attribute: onClick.", null, []),
    ("DONE not final", "<DONE><SH>ROOT</SH>",
        false, "DONE must be the final Agent command and occur once.", null, []),
    ("Duplicate DONE", "<SH>ROOT</SH><DONE><DONE>",
        false, "DONE must be the final Agent command and occur once.", null, []),
    ("Node limit", string.Concat(Enumerable.Repeat("<box />", 6001)) + "<SH>ROOT</SH>",
        false, "Document node limit exceeded.", null, []),
    ("Depth limit", string.Concat(Enumerable.Repeat("<box>", 34)) + "<SH>INERT</SH>" +
        string.Concat(Enumerable.Repeat("</box>", 34)) + "<SH>ROOT</SH>",
        false, "Document nesting exceeds limit.", null, []),
    ("SVG element limit", "<svg>" +
        string.Concat(Enumerable.Repeat("<circle r=\"2\"/>", 251)) + "</svg><SH>ROOT</SH>",
        true, null, null, ["SH"]),
    ("Document size", new string('x', 180_001) + "<SH>ROOT</SH>",
        false, "Message exceeds the document size limit.", null, []),
    ("Isolated high Unicode surrogate", "<box border={{label:\"\\uD83D\"}}>x</box>",
        true, null, "box", []),
    ("Isolated low Unicode surrogate", "<box border={{label:\"\\uDE00\"}}>x</box>",
        true, null, "box", []),
    ("Valid surrogate pair", "<box border={{label:\"\\uD83D\\uDE00\"}}>x</box>",
        true, null, null, []),
    ("High surrogate followed by non-low", "<box border={{label:\"\\uD83D\\u0041\"}}>x</box>",
        true, null, "box", []),
    ("Valid object and commands", "<box border={{size:2,color:\"danger\"}}>x</box><SH>ROOT</SH><DONE>",
        true, null, null, ["SH", "DONE"]),
    ("Unknown wrapper containment", "<Widget><SH>INERT</SH></Widget><ATTACH>file.txt</ATTACH><DONE>",
        true, null, "Widget", ["ATTACH", "DONE"]),
};
var parserFirstChance = 0;
void CountParserFirstChance(object? sender,
    System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
{
    // Check actual declaring types rather than a hardcoded stack-trace string.
    // Include private nested parser helpers; framework exceptions thrown from
    // inside parser calls are also counted because their stacks contain a parser frame.
    foreach (var frame in new System.Diagnostics.StackTrace(e.Exception, false).GetFrames() ?? [])
    {
        for (var type = frame.GetMethod()?.DeclaringType;
             type is not null; type = type.DeclaringType)
        {
            if (type != typeof(RichDocumentParser)) continue;
            parserFirstChance++;
            return;
        }
    }
}
AppDomain.CurrentDomain.FirstChanceException += CountParserFirstChance;
try
{
    foreach (var (name, source, valid, diagnostic, unknown, commands) in exceptionCases)
    {
        var before = parserFirstChance;
        var parsed = new RichDocumentParser(Registry.RichTags).Parse(source);
        var candidates = parsed.Commands.ToArray();
        var pass = parsed.Document.Valid == valid &&
            candidates.Select(c => c.Name).SequenceEqual(commands) &&
            (diagnostic is null || parsed.Document.Diagnostics.Any(d => d.Code == diagnostic)) &&
            (unknown is null || parsed.Document.Nodes.SelectMany(Walk)
                .Any(n => n.Kind == "unknown" && n.Name == unknown)) &&
            parserFirstChance == before;
        foreach (var candidate in candidates)
        {
            var span = candidate.SourceSpan;
            pass &= span.Start >= 0 && span.End <= source.Length &&
                span.Start < span.End &&
                source.AsSpan(span.Start, span.End - span.Start)
                    .StartsWith("<" + candidate.Name, StringComparison.Ordinal);
            if (candidate.Name is "SH" or "ATTACH")
                pass &= candidate.Body != "INERT";
        }
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: Exception-free {name}; " +
            $"valid={parsed.Document.Valid}; commands={candidates.Length}; parserFirstChance={parserFirstChance - before}");
        if (!pass) failures++;
    }
}
finally
{
    AppDomain.CurrentDomain.FirstChanceException -= CountParserFirstChance;
}
Console.WriteLine($"PARSER_FIRST_CHANCE_EXPECTED={parserFirstChance}");

Console.WriteLine($"{checks.Length + svgTests.Length + inlineTests.Length + 30 + exceptionCases.Length - failures}/{checks.Length + svgTests.Length + inlineTests.Length + 30 + exceptionCases.Length} passed");
return failures == 0 ? 0 : 1;
