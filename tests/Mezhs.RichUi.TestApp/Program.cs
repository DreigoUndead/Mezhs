using System.Text.Json;
using Mezhs.Common.RichUi;
using Mezhs.RichUi;
using Mezhs.Agent.Commands;

if (args.Contains("--parse-stdin"))
{
    // The Windows host console may default to an OEM code page, even when
    // Node feeds UTF-8 JSON bytes. Preserve non-ASCII radio choice payloads.
    Console.InputEncoding = System.Text.Encoding.UTF8;
    var inputs = JsonSerializer.Deserialize<string[]>(Console.In.ReadToEnd()) ?? [];
    var results = inputs.Select(source => new RichDocumentParser(AgentRichTags.Tags).Parse(source).Document).ToArray();
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
    ("Split escaped SH is literal", "<escape><SH></escape>echo YES<escape></SH></escape>", true, []),
    ("Split escaped ATTACH is literal", "<escape><ATTACH></escape>file.txt<escape></ATTACH></escape>", true, []),
    ("Split escaped closer whitespace is literal", "<escape><SH></escape>echo YES<escape> </SH> </escape>", true, []),
    ("Split escape without closer", "<escape><SH></escape>echo NO", true, []),
    ("Split escape with mismatched closer", "<escape><SH></escape>echo NO<escape></ATTACH></escape>", true, []),
    ("Other tags within escape remain inert", "<escape><text><SH>echo NO</SH></text><SH>echo YES</SH></escape>", true, ["SH"]),
    ("Escaped command nested inside box inert", "<box><escape><SH>echo NO</SH></escape></box>", true, []),
    ("Nested escape remains inert", "<escape><escape><SH>echo NO</SH></escape></escape>", true, []),
    ("Escaped DONE must be final", "<escape><DONE></escape><SH>echo NO</SH>", false, []),
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
    ("Same-line escape discovers commands", "Before <escape>raw <SH>EXECUTE</SH></escape> after", true, ["SH"]),
    ("Same-line registered containers match", "Before <box><text>inside</text></box> after", true, []),
    ("Same-line registered inline element matches", "Before <text strong>inside</text> after", true, []),
    ("Command inside escape is actionable", "<escape><SH>echo YES</SH></escape>", true, ["SH"]),
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
}.Concat(Registry.All.Select(command => (
    Name: $"Complete escaped {command.Name}",
    Source: command.Form == CommandForm.Marker
        ? $"<escape><{command.Name}></escape>"
        : $"<escape><{command.Name}>payload</{command.Name}></escape>",
    Valid: true,
    Commands: new[] { command.Name }
))).ToArray();
var failures = 0;
// Core-only UI parsing never promotes Agent command tags into actions.
var baseOnly = new RichDocumentParser(UiRichTags.Tags).Parse("<SH>not executable</SH>");
var baseOnlyPass = baseOnly.Document.Valid && baseOnly.Commands.Count == 0;
Console.WriteLine($"{(baseOnlyPass ? "PASS" : "FAIL")}: Core UI tags exclude Agent commands");
if (!baseOnlyPass) failures++;
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
    var result = new RichDocumentParser(AgentRichTags.Tags).Parse(source);
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
// Incomplete command delimiters inside escape wrappers remain literal, not paired.
var escapedSplitSource = "before<escape><SH></escape>\r\necho EXACT \r\n<escape></SH></escape>after";
var escapedSplit = new RichDocumentParser(AgentRichTags.Tags).Parse(escapedSplitSource);
var escapedSplitPass = escapedSplit.Document.Valid && escapedSplit.Commands.Count == 0 &&
    escapedSplit.Document.Nodes.Any(n => n.Kind == "literal" && n.Name == "escape" && n.Text == "<SH>");
Console.WriteLine($"{(escapedSplitPass ? "PASS" : "FAIL")}: Split escape wrappers remain inert");
if (!escapedSplitPass) failures++;
var escapedLiteral = new RichDocumentParser(AgentRichTags.Tags).Parse("<escape>before <SH>echo HIDE</SH> after</escape>");
var escapedLiteralPass = escapedLiteral.Document.Valid &&
    escapedLiteral.Commands.Count == 1 &&
    escapedLiteral.Document.Nodes.Any(n => n.Kind == "literal" && n.Text == "before  after");
Console.WriteLine($"{(escapedLiteralPass ? "PASS" : "FAIL")}: Escaped command hidden from literal display");
if (!escapedLiteralPass) failures++;

// A single escaped command is an inline hidden command, not a paragraph break.
var soleEscaped = "Before <escape><SH>echo HIDDEN</SH></escape> after";
var soleEscapedDoc = new RichDocumentParser(AgentRichTags.Tags).Parse(soleEscaped);
var soleEscapedParagraph = soleEscapedDoc.Document.Nodes.SingleOrDefault();
var soleEscapedPass = soleEscapedDoc.Document.Valid &&
    soleEscapedDoc.Commands.Count == 1 &&
    soleEscapedDoc.Commands[0].SourceSpan.Start == "Before ".Length &&
    soleEscapedDoc.Commands[0].SourceSpan.End == soleEscaped.Length - " after".Length &&
    soleEscapedParagraph?.Kind == "paragraph" &&
    soleEscapedParagraph.Children?.Count == 2 &&
    soleEscapedParagraph.Children[0].Text == "Before " &&
    soleEscapedParagraph.Children[1].Text == " after";
Console.WriteLine($"{(soleEscapedPass ? "PASS" : "FAIL")}: Sole escaped command preserves Markdown paragraph and full wrapper span");
if (!soleEscapedPass) failures++;

// Regression: the rendered tree, not only VisibleContent, must keep Markdown flow.
var shellOpen = string.Concat((char)60, "SH", (char)62);
var shellClose = string.Concat((char)60, "/SH", (char)62);
var shellTag = shellOpen + "echo HIDDEN" + shellClose;
var inlineSource = "Before " + shellTag + " after";
var inlineDoc = new RichDocumentParser(AgentRichTags.Tags).Parse(inlineSource);
var paragraph = inlineDoc.Document.Nodes.SingleOrDefault();
var inlineOk = inlineDoc.Document.Valid && inlineDoc.Commands.Count == 1 &&
    inlineDoc.Commands[0].Body == "echo HIDDEN" && paragraph?.Kind == "paragraph" &&
    paragraph.Children?.Count == 2 && paragraph.Children[0].Text == "Before " &&
    paragraph.Children[1].Text == " after" &&
    paragraph.Children[0].SourceSpan.End == inlineSource.IndexOf(shellOpen, StringComparison.Ordinal) &&
    paragraph.Children[1].SourceSpan.Start == inlineSource.IndexOf(" after", StringComparison.Ordinal);
// Generic syntax layer: literal object attributes, unknown envelopes, and pipes.
var objectDoc = new RichDocumentParser(AgentRichTags.Tags).Parse("<box border={{size:2,color:\"danger\"}}>Red</box>");
var objectJson = objectDoc.Document.Nodes.SelectMany(Walk).FirstOrDefault(n => n.Name == "box")?
    .Attributes?.GetValueOrDefault("border");
var objectPass = objectDoc.Document.Valid && objectJson?.StartsWith("json:", StringComparison.Ordinal) == true &&
    Uri.UnescapeDataString(objectJson[5..]).Contains("\"danger\"", StringComparison.Ordinal);
Console.WriteLine($"{(objectPass ? "PASS" : "FAIL")}: Bounded JSX object literal is serialized as inert JSON");
if (!objectPass) failures++;
var dynamicDoc = new RichDocumentParser(AgentRichTags.Tags).Parse("<box border={{size:alert(1)}}>unsafe</box>");
var dynamicPass = dynamicDoc.Document.Valid && dynamicDoc.Commands.Count == 0 &&
    dynamicDoc.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "unknown" && n.Name == "box");
Console.WriteLine($"{(dynamicPass ? "PASS" : "FAIL")}: Dynamic expression falls back to inert unknown control");
if (!dynamicPass) failures++;
var unknownEnvelope = new RichDocumentParser(AgentRichTags.Tags).Parse("<Widget><SH>inert</SH></Widget><SH>root</SH>");
var unknownPass = unknownEnvelope.Document.Valid && unknownEnvelope.Commands.Count == 1 &&
    unknownEnvelope.Commands[0].Body == "root" &&
    unknownEnvelope.Document.Nodes.Any(n => n.Kind == "unknown" && n.Name == "Widget");
Console.WriteLine($"{(unknownPass ? "PASS" : "FAIL")}: Unknown wrapper preserves root-only command boundary");
if (!unknownPass) failures++;
var tableDoc = new RichDocumentParser(AgentRichTags.Tags).Parse("| A | B |\\n|---|---|\\n| 1 | 2 |".Replace("\\n", "\n"));
var tablePass = tableDoc.Document.Valid && tableDoc.Document.Nodes.SelectMany(Walk)
    .Any(n => n.Kind == "table" && n.Children?.Any(c => c.Kind == "tableRow") == true);
Console.WriteLine($"{(tablePass ? "PASS" : "FAIL")}: Pipe table remains a generic Markdown syntax transformation");
if (!tablePass) failures++;
var htmlWrapperSource = "<widget>\\n<text>inside</text>\\n</widget>\\n<SH>ROOT</SH>".Replace("\\n", "\n");
var htmlWrapperDoc = new RichDocumentParser(AgentRichTags.Tags).Parse(htmlWrapperSource);
var htmlWrapperPass = htmlWrapperDoc.Document.Valid && htmlWrapperDoc.Commands.Count == 1 &&
    htmlWrapperDoc.Commands[0].Body == "ROOT" &&
    htmlWrapperDoc.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "unknown" && n.Name == "widget");
Console.WriteLine($"{(htmlWrapperPass ? "PASS" : "FAIL")}: Multiline unknown HTML wrapper cannot discard root commands");
if (!htmlWrapperPass) failures++;
var commentSource = "<!--\\n<text>hello</text>\\n-->\\n<SH>ROOT</SH>".Replace("\\n", "\n");
var commentDoc = new RichDocumentParser(AgentRichTags.Tags).Parse(commentSource);
var commentPass = commentDoc.Document.Valid && commentDoc.Commands.Count == 1 &&
    commentDoc.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "element" && n.Name == "text");
Console.WriteLine($"{(commentPass ? "PASS" : "FAIL")}: HTML literal lowering restores rich elements");
if (!commentPass) failures++;
var fenceInfoSource = "~~~<text>info</text>\\ncontent\\n~~~\\n<SH>ROOT</SH>".Replace("\\n", "\n");
var fenceInfoDoc = new RichDocumentParser(AgentRichTags.Tags).Parse(fenceInfoSource);
var fenceInfoPass = fenceInfoDoc.Document.Valid && fenceInfoDoc.Commands.Count == 1 &&
    fenceInfoDoc.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "element" && n.Name == "text");
Console.WriteLine($"{(fenceInfoPass ? "PASS" : "FAIL")}: Fence info literal survives lowering without losing root shell");
if (!fenceInfoPass) failures++;


Console.WriteLine($"{(inlineOk ? "PASS" : "FAIL")}: Rendered inline command retains one paragraph and exact spans");
if (!inlineOk) failures++;
var strongDoc = new RichDocumentParser(AgentRichTags.Tags).Parse("**Before " + shellTag + " after**");
var strongNode = strongDoc.Document.Nodes.SingleOrDefault()?.Children?.SingleOrDefault();
var strongOk = strongDoc.Document.Valid && strongNode?.Kind == "strong" &&
    strongNode.Children?.Count == 2 && strongNode.Children[0].Text == "Before " &&
    strongNode.Children[1].Text == " after";
Console.WriteLine($"{(strongOk ? "PASS" : "FAIL")}: Rendered command preserves spanning strong emphasis");
if (!strongOk) failures++;
var listDoc = new RichDocumentParser(AgentRichTags.Tags).Parse("- Before " + shellTag + " after");
var listParagraphs = listDoc.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "paragraph").ToArray();
var listOk = listDoc.Document.Valid && listDoc.Document.Nodes.Count == 1 &&
    listDoc.Document.Nodes[0].Kind == "list" && listParagraphs.Length == 1 &&
    listParagraphs[0].Children?.Count == 2;
Console.WriteLine($"{(listOk ? "PASS" : "FAIL")}: Rendered command retains one Markdown list item");
if (!listOk) failures++;
var codeDoc = new RichDocumentParser(AgentRichTags.Tags).Parse("`pre " + shellTag + " post`");
var codeNodes = codeDoc.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "inlineCode").ToArray();
var codeOk = codeDoc.Document.Valid && codeNodes.Length == 1 && codeNodes[0].Text == "pre  post";
Console.WriteLine($"{(codeOk ? "PASS" : "FAIL")}: Removed command leaves intact inline code text");
if (!codeOk) failures++;
var fenceDoc = new RichDocumentParser(AgentRichTags.Tags).Parse("~~~\npre " + shellTag + " post\n~~~");
var fenceNodes = fenceDoc.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "code").ToArray();
var fenceOk = fenceDoc.Document.Valid && fenceNodes.Length == 1 &&
    fenceNodes[0].Text?.Contains("pre  post", StringComparison.Ordinal) == true;
Console.WriteLine($"{(fenceOk ? "PASS" : "FAIL")}: Removed command leaves intact fenced code text");
if (!fenceOk) failures++;
// A command surrounded by blank lines must not leave an empty paragraph.
var blankLineDoc = new RichDocumentParser(AgentRichTags.Tags).Parse("Before\n\n" + shellTag + "\n\nAfter");
var blankLineOk = blankLineDoc.Document.Valid && blankLineDoc.Commands.Count == 1 &&
    blankLineDoc.Document.Nodes.Count == 2 &&
    blankLineDoc.Document.Nodes.All(n => n.Kind == "paragraph" && n.Children?.Count == 1) &&
    blankLineDoc.Document.Nodes[0].Children![0].Text == "Before" &&
    blankLineDoc.Document.Nodes[1].Children![0].Text == "After";
Console.WriteLine($"{(blankLineOk ? "PASS" : "FAIL")}: Standalone command leaves no empty rendered paragraph");
if (!blankLineOk) failures++;

// Every tag carries independent child-search and child-eligibility policies.
var controlsPass =
    AgentRichTags.Tags.TryGet("box", out var boxDef) &&
    boxDef.SearchChildren && boxDef.AllowAsChild &&
    AgentRichTags.Tags.TryGet("row", out var rowDef) &&
    rowDef.SearchChildren && rowDef.AllowAsChild &&
    AgentRichTags.Tags.TryGet("SH", out var shellDef) &&
    !shellDef.SearchChildren && !shellDef.AllowAsChild &&
    AgentRichTags.Tags.TryGet("ATTACH", out var attachDef) &&
    !attachDef.SearchChildren && !attachDef.AllowAsChild &&
    AgentRichTags.Tags.TryGet("DONE", out var doneDef) &&
    !doneDef.SearchChildren && !doneDef.AllowAsChild &&
    AgentRichTags.Tags.TryGet("escape", out var escapeDef) &&
    !escapeDef.SearchChildren && escapeDef.AllowAsChild &&
    AgentRichTags.Tags.TryGet("WritingBlock", out var writingDef) &&
    !writingDef.SearchChildren && writingDef.AllowAsChild;
Console.WriteLine($"{(controlsPass ? "PASS" : "FAIL")}: Independent per-tag child search and child eligibility policies");
if (!controlsPass) failures++;

// A model can literally author the former private-use marker sequence.
// The restored inline node must stay at its real UTF-16 position.
var collisionSource = "pre " + (char)0xE000 + (char)0xE001 + " <text strong>node</text> post";
var collisionResult = new RichDocumentParser(AgentRichTags.Tags).Parse(collisionSource);
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
var taskResult = new RichDocumentParser(AgentRichTags.Tags).Parse(taskSource);
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
var capturedDocument = new RichDocumentParser(AgentRichTags.Tags).Parse(capturedLayout);
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
var rootIndented = new RichDocumentParser(AgentRichTags.Tags).Parse("Intro\\n\\n    actual code".Replace("\\n", "\n"));
var rootIndentedPass = rootIndented.Document.Valid &&
    rootIndented.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "code");
Console.WriteLine($"{(rootIndentedPass ? "PASS" : "FAIL")}: Root Markdown retains four-space code blocks");
if (!rootIndentedPass) failures++;
var containerFenced = new RichDocumentParser(AgentRichTags.Tags).Parse("<box>\\n  ~~~\\n  code inside rich box\\n  ~~~\\n</box>".Replace("\\n", "\n"));
var containerFencedPass = containerFenced.Document.Valid &&
    containerFenced.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "code" &&
        n.Text?.Contains("code inside rich box") == true);
Console.WriteLine($"{(containerFencedPass ? "PASS" : "FAIL")}: Explicit fenced code remains available inside rich layout");
if (!containerFencedPass) failures++;

var fixture = File.ReadAllText(Path.Combine("tests", "fixtures", "rich-model-ui", "custom-controls-question.md"));
var parsedQuestion = new RichDocumentParser(AgentRichTags.Tags).Parse(fixture);
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
var wrappedResult = new RichDocumentParser(AgentRichTags.Tags).Parse(wrappedFixture);
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
var radioCodeResult = new RichDocumentParser(AgentRichTags.Tags).Parse(radioCode);
var radioCodeNode = radioCodeResult.Document.Nodes.SingleOrDefault(n => n.Kind == "question");
var radioCodeOption = radioCodeNode?.Children?.FirstOrDefault(n => n.Kind == "questionOption");
var radioCodeButton = radioCodeNode?.Children?.FirstOrDefault(n => n.Kind == "questionButton");
var radioCodePass = radioCodeResult.Document.Valid && radioCodeResult.Commands.Count == 0 &&
    radioCodeOption?.Children?.SelectMany(Walk).Any(n => n.Kind == "inlineCode" && n.Text == "</radio>") == true &&
    radioCodeButton?.Children?.SelectMany(Walk).Any(n => n.Kind == "inlineCode" && n.Text == "</button>") == true &&
    radioCodeOption.SourceSpan.End == radioCode.IndexOf("    </radio>", StringComparison.Ordinal) + 12;
Console.WriteLine($"{(radioCodePass ? "PASS" : "FAIL")}: Radio and button delimiters inside inline Markdown code remain inert with original spans");
if (!radioCodePass) failures++;

var rejectedBox = new RichDocumentParser(AgentRichTags.Tags).Parse(wrappedFixture.Replace("<box border", "<box onClick=\"unsafe()\" border"));
var rejectedBoxPass = rejectedBox.Document.Valid && rejectedBox.Commands.Count == 0 &&
    rejectedBox.Document.Nodes.SelectMany(Walk).Any(n => n.Kind == "unknown" && n.Name == "box");
Console.WriteLine($"{(rejectedBoxPass ? "PASS" : "FAIL")}: Unsafe radio box wrapper remains inert");
if (!rejectedBoxPass) failures++;

var staticStyle = new RichDocumentParser(AgentRichTags.Tags).Parse("<text textAlign=\"center\" tabularNums>42</text><title textAlign=\"center\" tabularNums>Title</title>");
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
    var parsed = new RichDocumentParser(AgentRichTags.Tags).Parse(source);
    var pass = verify(parsed);
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {name}; valid={parsed.Document.Valid}; errors={string.Join(",", parsed.Document.Diagnostics.Select(d => d.Code))}");
    if (!pass) failures++;
}

var svgShellAttachSource = "<svg>before<SH>echo HIDDEN</SH>middle<ATTACH>path.txt</ATTACH>after<circle r='2'/></svg>";
var svgWrappedShellSource = "<svg><widget>before<SH>echo HIDDEN</SH>after</widget></svg>";
var svgTests = new (string Name, string Source, bool Valid, Func<RichParseResult, bool> Extra)[]
{
    ("Representative SVG", File.ReadAllText(Path.Combine("tests","fixtures","rich-model-ui","representative-svg.md")), true, r => r.Document.Nodes.Any(n => n.Kind == "element" && n.Name == "svg" && n.Children?.Any(c => c.Name == "text") == true && n.Children?.Any(c => c.Name == "g" && c.Children?.Any(x => x.Name == "rect") == true && c.Children?.Any(x => x.Name == "text" && x.Children?.Any(t => t.Text?.Contains("Collector") == true) == true) == true) == true)),
    ("SVG embedded SH is inert", "<svg viewBox=\"0 0 100 100\"><SH>echo YES</SH></svg>", true, r => r.Commands.Count == 0),
    ("SVG keeps nested SH and ATTACH inert", svgShellAttachSource, true, r =>
        r.Commands.Count == 0 &&
        r.Document.Nodes.Single().Children?.Any(n => n.Kind == "text" &&
            n.Text?.Contains("<SH>echo HIDDEN</SH>middle<ATTACH>path.txt</ATTACH>", StringComparison.Ordinal) == true) == true &&
        r.Document.Nodes.Single().Children?.Any(n => n.Name == "circle") == true),
    ("SVG unknown wrapper keeps nested SH inert", svgWrappedShellSource, true, r =>
        r.Commands.Count == 0 &&
        r.Document.Nodes.Single().Kind == "unknown" &&
        r.Document.Nodes.Single().Name == "svg"),
    ("SVG injected event rejected", "<svg onload=\"alert(1)\"></svg>", true, r => r.Commands.Count == 0),
    ("SVG forbidden url rejected", "<svg><rect x=\"1\" fill=\"url(#evil)\" /></svg>", true, r => r.Commands.Count == 0),
    ("SVG text namespace accepted", "<svg viewBox=\"0 0 80 80\"><text x=\"15\" y=\"30\" fontSize=\"12\" >Hello</text></svg>", true, r => r.Document.Nodes.Any(n => n.Name == "svg" && n.Children?.Any(c => c.Name == "text" && c.Children?.Any(t => t.Text == "Hello") == true) == true)),
    ("SVG nested foreign UI rejected", "<svg><box>bad</box></svg>", true, r => r.Commands.Count == 0),
    ("SVG foreign UI under unknown wrapper rejected", "<svg><widget><box>bad</box></widget></svg>", true, r => r.Commands.Count == 0),
    ("SVG unknown wrapper is diagnostic", "<svg><widget>literal</widget></svg>", true, r => r.Document.Nodes.Any(n => n.Kind == "unknown" && n.Name == "svg")),
    ("SVG unknown wrapper keeps SH inert", "<svg><widget><SH>echo YES</SH></widget></svg>", true, r => r.Commands.Count == 0 && r.Document.Nodes.Any(n => n.Kind == "unknown")),
    ("SVG nested SVG rejected", "<svg><svg></svg></svg>", true, r => r.Commands.Count == 0),
    ("SVG geometry over bound rejected", "<svg><circle r=\"100000000\" /></svg>", true, r => r.Commands.Count == 0),
};
foreach (var (name, source, valid, extra) in svgTests)
{
    var result = new RichDocumentParser(AgentRichTags.Tags).Parse(source);
    var pass = result.Document.Valid == valid && extra(result);
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {name}; valid={result.Document.Valid}; errors={string.Join(",", result.Document.Diagnostics.Select(d => d.Code))}");
    if (!pass) failures++;
}
// Position-independent matching includes markup-looking tags within Markdown.
var nestedSource = "<box><box><text>Nested sibling</text></box> outer</box>";
var nestedResult = new RichDocumentParser(AgentRichTags.Tags).Parse(nestedSource);
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
var rawResult = new RichDocumentParser(AgentRichTags.Tags).Parse(rawSiblings);
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
var wideParsed = new RichDocumentParser(AgentRichTags.Tags).Parse(wideSource);
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
var deepParsed = new RichDocumentParser(AgentRichTags.Tags).Parse(deepSource);
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
var alternatingParsed = new RichDocumentParser(AgentRichTags.Tags).Parse(alternatingSource);
timer.Stop();
var bytesAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
var inlineNodes = alternatingParsed.Document.Nodes.SelectMany(Walk).Count(n => n.Name == "text");
var alternatingPass = alternatingParsed.Document.Valid && alternatingParsed.Commands.Count == 0 &&
    inlineNodes == 600 && timer.Elapsed < TimeSpan.FromSeconds(8) &&
    bytesAllocated < 128L * 1024 * 1024;
Console.WriteLine($"{(alternatingPass ? "PASS" : "FAIL")}: 600 alternating rich/Markdown inlines in {timer.ElapsedMilliseconds} ms, {bytesAllocated / 1024:N0} KiB allocated; rich nodes={inlineNodes}");
if (!alternatingPass) failures++;

Console.WriteLine($"{checks.Length + svgTests.Length + inlineTests.Length + 18 - failures}/{checks.Length + svgTests.Length + inlineTests.Length + 18} passed");
return failures == 0 ? 0 : 1;
