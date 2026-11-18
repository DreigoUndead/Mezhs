using System.Text.Json;
using Mezhs.Common.RichUi;

if (args.Contains("--parse-stdin"))
{
    // The Windows host console may default to an OEM code page, even when
    // Node feeds UTF-8 JSON bytes. Preserve non-ASCII radio choice payloads.
    Console.InputEncoding = System.Text.Encoding.UTF8;
    var inputs = JsonSerializer.Deserialize<string[]>(Console.In.ReadToEnd()) ?? [];
    var results = inputs.Select(source => new RichDocumentParser().Parse(source).Document).ToArray();
    Console.Write(JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

var checks = new (string Name, string Source, bool Valid, string[] Commands)[]
{
    ("Plain Markdown", "# Hello\n\n**Strong** and [link](https://example.com)", true, []),
    ("Nested box and text", "<box gap={2}>\n<text>hello</text>\n</box>", true, []),
    ("Unknown presentation attribute is inert", "<text futureTone=\"violet\" strong>Visible</text>", true, []),
    ("Unsafe HTML attribute rejected", "<badge style=\"color:red\">No</badge>", false, []),
    ("Unsafe event attribute blocks entire batch", "<box onClick=\"alert(1)\"><SH>echo NO</SH></box>", false, []),
    ("Unknown SVG attribute rejected", "<svg futureTheme=\"violet\"><rect width=\"5\"/></svg>", false, []),
    ("Citation ID supported", "<Cite ref=\"turn0search0\"/>", true, []),
    ("Citation arbitrary metadata rejected", "<Cite futureSource=\"javascript:evil\"/>", false, []),
    ("Inline nested tags", "<text>Before <badge>status</badge> and <Link url=\"https://example.com\">link</Link> after</text>", true, []),
    ("Inline text rejects block child", "<text>Before <box>block</box> after</text>", false, []),
    ("Inline caption rejects list", "<caption>Start\n\n- One\n- Two\n</caption>", false, []),
    ("Rich Link rejects nested links", "<Link url=\"https://a.test\">Outer <Link url=\"https://b.test\">Inner</Link></Link>", false, []),
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
    ("Command inside text", "<text>Hi <SH>echo YES</SH> there</text>", true, ["SH"]),
    ("Command inside Link", "<Link url=\"https://example.com\"><ATTACH>file.txt</ATTACH></Link>", true, ["ATTACH"]),
    ("Command inside CodeBlock", "<CodeBlock>before <SH>echo YES</SH> after</CodeBlock>", true, ["SH"]),
    ("Command inside escape", "<escape><SH>echo YES</SH></escape>", true, ["SH"]),
    ("Command inside Markdown fence", "~~~\n<SH>echo YES</SH>\n~~~", true, ["SH"]),
    ("Command inside inline code", "`<SH>echo YES</SH>`", true, ["SH"]),
    ("Command inside Markdown comment", "<!-- <SH>echo YES</SH> -->", true, ["SH"]),
    ("Command inside quote", "> <SH>echo YES</SH>", true, ["SH"]),
    ("Command inside SVG", "<svg viewBox=\"0 0 20 20\"><SH>echo YES</SH><circle r=\"2\"/></svg>", true, ["SH"]),
    ("Opaque command bodies", "<SH>before <ATTACH>nested</ATTACH></SH>", true, ["SH"]),
    ("Unmatched closing tag ignored", "before </SH> after", true, []),
    ("Empty SH parsed", "<SH></SH>", true, ["SH"]),
    ("DONE must be final", "<DONE><SH>echo NO</SH>", false, []),
    ("Duplicate DONE rejected", "<DONE><DONE>", false, []),
    ("Unknown complete tag rejected", "<UNKNOWN>text</UNKNOWN>", false, []),
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
    var result = new RichDocumentParser().Parse(source);
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
// Regression: the rendered tree, not only VisibleContent, must keep Markdown flow.
var shellOpen = string.Concat((char)60, "SH", (char)62);
var shellClose = string.Concat((char)60, "/SH", (char)62);
var shellTag = shellOpen + "echo HIDDEN" + shellClose;
var inlineSource = "Before " + shellTag + " after";
var inlineDoc = new RichDocumentParser().Parse(inlineSource);
var paragraph = inlineDoc.Document.Nodes.SingleOrDefault();
var inlineOk = inlineDoc.Document.Valid && inlineDoc.Commands.Count == 1 &&
    inlineDoc.Commands[0].Body == "echo HIDDEN" && paragraph?.Kind == "paragraph" &&
    paragraph.Children?.Count == 2 && paragraph.Children[0].Text == "Before " &&
    paragraph.Children[1].Text == " after" &&
    paragraph.Children[0].SourceSpan.End == inlineSource.IndexOf(shellOpen, StringComparison.Ordinal) &&
    paragraph.Children[1].SourceSpan.Start == inlineSource.IndexOf(" after", StringComparison.Ordinal);
Console.WriteLine($"{(inlineOk ? "PASS" : "FAIL")}: Rendered inline command retains one paragraph and exact spans");
if (!inlineOk) failures++;
var strongDoc = new RichDocumentParser().Parse("**Before " + shellTag + " after**");
var strongNode = strongDoc.Document.Nodes.SingleOrDefault()?.Children?.SingleOrDefault();
var strongOk = strongDoc.Document.Valid && strongNode?.Kind == "strong" &&
    strongNode.Children?.Count == 2 && strongNode.Children[0].Text == "Before " &&
    strongNode.Children[1].Text == " after";
Console.WriteLine($"{(strongOk ? "PASS" : "FAIL")}: Rendered command preserves spanning strong emphasis");
if (!strongOk) failures++;
var listDoc = new RichDocumentParser().Parse("- Before " + shellTag + " after");
var listParagraphs = listDoc.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "paragraph").ToArray();
var listOk = listDoc.Document.Valid && listDoc.Document.Nodes.Count == 1 &&
    listDoc.Document.Nodes[0].Kind == "list" && listParagraphs.Length == 1 &&
    listParagraphs[0].Children?.Count == 2;
Console.WriteLine($"{(listOk ? "PASS" : "FAIL")}: Rendered command retains one Markdown list item");
if (!listOk) failures++;
var codeDoc = new RichDocumentParser().Parse("`pre " + shellTag + " post`");
var codeNodes = codeDoc.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "inlineCode").ToArray();
var codeOk = codeDoc.Document.Valid && codeNodes.Length == 1 && codeNodes[0].Text == "pre  post";
Console.WriteLine($"{(codeOk ? "PASS" : "FAIL")}: Removed command leaves intact inline code text");
if (!codeOk) failures++;
var fenceDoc = new RichDocumentParser().Parse("~~~\npre " + shellTag + " post\n~~~");
var fenceNodes = fenceDoc.Document.Nodes.SelectMany(Walk).Where(n => n.Kind == "code").ToArray();
var fenceOk = fenceDoc.Document.Valid && fenceNodes.Length == 1 &&
    fenceNodes[0].Text?.Contains("pre  post", StringComparison.Ordinal) == true;
Console.WriteLine($"{(fenceOk ? "PASS" : "FAIL")}: Removed command leaves intact fenced code text");
if (!fenceOk) failures++;
// A command surrounded by blank lines must not leave an empty paragraph.
var blankLineDoc = new RichDocumentParser().Parse("Before\n\n" + shellTag + "\n\nAfter");
var blankLineOk = blankLineDoc.Document.Valid && blankLineDoc.Commands.Count == 1 &&
    blankLineDoc.Document.Nodes.Count == 2 &&
    blankLineDoc.Document.Nodes.All(n => n.Kind == "paragraph" && n.Children?.Count == 1) &&
    blankLineDoc.Document.Nodes[0].Children![0].Text == "Before" &&
    blankLineDoc.Document.Nodes[1].Children![0].Text == "After";
Console.WriteLine($"{(blankLineOk ? "PASS" : "FAIL")}: Standalone command leaves no empty rendered paragraph");
if (!blankLineOk) failures++;

// Every tag carries independent child-search and child-eligibility policies.
var controlsPass =
    RichTagRegistry.TryGet("box", out var boxDef) &&
    boxDef.SearchChildren && boxDef.AllowAsChild &&
    RichTagRegistry.TryGet("row", out var rowDef) &&
    rowDef.SearchChildren && rowDef.AllowAsChild &&
    RichTagRegistry.TryGet("SH", out var shellDef) &&
    !shellDef.SearchChildren && shellDef.AllowAsChild &&
    RichTagRegistry.TryGet("ATTACH", out var attachDef) &&
    !attachDef.SearchChildren && attachDef.AllowAsChild &&
    RichTagRegistry.TryGet("DONE", out var doneDef) &&
    !doneDef.SearchChildren && !doneDef.AllowAsChild &&
    RichTagRegistry.TryGet("escape", out var escapeDef) &&
    !escapeDef.SearchChildren && escapeDef.AllowAsChild &&
    RichTagRegistry.TryGet("WritingBlock", out var writingDef) &&
    !writingDef.SearchChildren && writingDef.AllowAsChild;
Console.WriteLine($"{(controlsPass ? "PASS" : "FAIL")}: Independent per-tag child search and child eligibility policies");
if (!controlsPass) failures++;

// A model can literally author the former private-use marker sequence.
// The restored inline node must stay at its real UTF-16 position.
var collisionSource = "pre " + (char)0xE000 + (char)0xE001 + " <text strong>node</text> post";
var collisionResult = new RichDocumentParser().Parse(collisionSource);
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
var taskResult = new RichDocumentParser().Parse(taskSource);
var taskNodes = taskResult.Document.Nodes.SelectMany(Walk)
    .Where(node => node.Kind == "taskCheckbox").ToArray();
var taskPass = taskResult.Document.Valid && taskResult.Commands.Count == 0 &&
    taskNodes.Length == 2 && taskNodes[0].Attributes?.GetValueOrDefault("checked") == "true" &&
    taskNodes[1].Attributes?.GetValueOrDefault("checked") == "false";
Console.WriteLine($"{(taskPass ? "PASS" : "FAIL")}: GFM task checkboxes remain accessible inside Markdown paragraphs");
if (!taskPass) failures++;

var fixture = File.ReadAllText(Path.Combine("tests", "fixtures", "rich-model-ui", "custom-controls-question.md"));
var parsedQuestion = new RichDocumentParser().Parse(fixture);
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
var wrappedResult = new RichDocumentParser().Parse(wrappedFixture);
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
var radioCodeResult = new RichDocumentParser().Parse(radioCode);
var radioCodeNode = radioCodeResult.Document.Nodes.SingleOrDefault(n => n.Kind == "question");
var radioCodeOption = radioCodeNode?.Children?.FirstOrDefault(n => n.Kind == "questionOption");
var radioCodeButton = radioCodeNode?.Children?.FirstOrDefault(n => n.Kind == "questionButton");
var radioCodePass = radioCodeResult.Document.Valid && radioCodeResult.Commands.Count == 0 &&
    radioCodeOption?.Children?.SelectMany(Walk).Any(n => n.Kind == "inlineCode" && n.Text == "</radio>") == true &&
    radioCodeButton?.Children?.SelectMany(Walk).Any(n => n.Kind == "inlineCode" && n.Text == "</button>") == true &&
    radioCodeOption.SourceSpan.End == radioCode.IndexOf("    </radio>", StringComparison.Ordinal) + 12;
Console.WriteLine($"{(radioCodePass ? "PASS" : "FAIL")}: Radio and button delimiters inside inline Markdown code remain inert with original spans");
if (!radioCodePass) failures++;

var rejectedBox = new RichDocumentParser().Parse(wrappedFixture.Replace("<box border", "<box onClick=\"unsafe()\" border"));
var rejectedBoxPass = !rejectedBox.Document.Valid && rejectedBox.Commands.Count == 0;
Console.WriteLine($"{(rejectedBoxPass ? "PASS" : "FAIL")}: Unsafe radio box wrapper remains inert");
if (!rejectedBoxPass) failures++;

var staticStyle = new RichDocumentParser().Parse("<text textAlign=\"center\" tabularNums>42</text><title textAlign=\"center\" tabularNums>Title</title>");
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
    var parsed = new RichDocumentParser().Parse(source);
    var pass = verify(parsed);
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {name}; valid={parsed.Document.Valid}; errors={string.Join(",", parsed.Document.Diagnostics.Select(d => d.Code))}");
    if (!pass) failures++;
}

var svgTests = new (string Name, string Source, bool Valid, Func<RichParseResult, bool> Extra)[]
{
    ("Representative SVG", File.ReadAllText(Path.Combine("tests","fixtures","rich-model-ui","representative-svg.md")), true, r => r.Document.Nodes.Any(n => n.Kind == "element" && n.Name == "svg" && n.Children?.Any(c => c.Name == "text") == true && n.Children?.Any(c => c.Name == "g" && c.Children?.Any(x => x.Name == "rect") == true && c.Children?.Any(x => x.Name == "text" && x.Children?.Any(t => t.Text?.Contains("Collector") == true) == true) == true) == true)),
    ("SVG embedded commands recognized", "<svg viewBox=\"0 0 100 100\"><SH>echo YES</SH></svg>", true, r => r.Commands.Count == 1 && r.Commands[0].Name == "SH"),
    ("SVG injected event rejected", "<svg onload=\"alert(1)\"></svg>", false, r => r.Commands.Count == 0),
    ("SVG forbidden url rejected", "<svg><rect x=\"1\" fill=\"url(#evil)\" /></svg>", false, r => r.Commands.Count == 0),
    ("SVG text namespace accepted", "<svg viewBox=\"0 0 80 80\"><text x=\"15\" y=\"30\" fontSize=\"12\" >Hello</text></svg>", true, r => r.Document.Nodes.Any(n => n.Name == "svg" && n.Children?.Any(c => c.Name == "text" && c.Children?.Any(t => t.Text == "Hello") == true) == true)),
    ("SVG nested foreign UI rejected", "<svg><box>bad</box></svg>", false, r => r.Commands.Count == 0),
    ("SVG nested SVG rejected", "<svg><svg></svg></svg>", false, r => r.Commands.Count == 0),
    ("SVG geometry over bound rejected", "<svg><circle r=\"100000000\" /></svg>", false, r => r.Commands.Count == 0),
};
foreach (var (name, source, valid, extra) in svgTests)
{
    var result = new RichDocumentParser().Parse(source);
    var pass = result.Document.Valid == valid && extra(result);
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {name}; valid={result.Document.Valid}; errors={string.Join(",", result.Document.Diagnostics.Select(d => d.Code))}");
    if (!pass) failures++;
}
// Position-independent matching includes markup-looking tags within Markdown.
var nestedSource = "<box><box><text>Nested sibling</text></box> outer</box>";
var nestedResult = new RichDocumentParser().Parse(nestedSource);
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
var rawResult = new RichDocumentParser().Parse(rawSiblings);
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
var wideParsed = new RichDocumentParser().Parse(wideSource);
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
var deepParsed = new RichDocumentParser().Parse(deepSource);
timer.Stop();
var deepPass = deepParsed.Document.Valid && deepParsed.Commands.Count == 1 &&
    timer.Elapsed < TimeSpan.FromSeconds(8);
Console.WriteLine($"{(deepPass ? "PASS" : "FAIL")}: Nested protected code bounded in {timer.ElapsedMilliseconds} ms");
if (!deepPass) failures++;

var alternatingSource = string.Concat(Enumerable.Range(0, 600)
    .Select(i => $"Part {i} <text strong>Value {i}</text> "));
GC.Collect();
var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
timer.Restart();
var alternatingParsed = new RichDocumentParser().Parse(alternatingSource);
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
