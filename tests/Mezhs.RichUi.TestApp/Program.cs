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
    ("Opaque escape", "<escape>\n<SH>\necho NEVER\n</SH>\n<DONE>\n</escape>", true, []),
    ("Shell body and DONE", "<SH>\r\necho first\r\n<DONE>\r\n</SH>\r\n<DONE>", true, ["SH", "DONE"]),
    ("Nested command", "<box>\n<SH>\necho nested\n</SH>\n</box>\n<DONE>", true, ["SH", "DONE"]),
    ("Command in grid item", "<grid>\n<grid-item>\n<SH>\necho nested\n</SH>\n</grid-item>\n</grid>", true, ["SH"]),
    ("Command in text denied", "<text>\n<SH>\necho NEVER\n</SH>\n</text>", false, []),
    ("ATTACH in Link denied", "<Link url=\"https://example.com\">\n<ATTACH>\nfile.txt\n</ATTACH>\n</Link>", false, []),
    ("DONE in badge denied", "<badge>\n<DONE>\n</badge>", false, []),
    ("Command in title denied", "<title>\n<SH>\necho NEVER\n</SH>\n</title>", false, []),
    ("Command inside Markdown list", "- step\n  <SH>\n  echo listed\n  </SH>\n\n<DONE>", true, ["SH", "DONE"]),
    ("Fenced command inside box list", "<box>\n- Example\n  ~~~text\n  <SH>\n  echo NEVER\n  </SH>\n  ~~~\n</box>", true, []),
    ("Fenced command at root", "~~~text\n<SH>\necho NEVER\n</SH>\n~~~", true, []),
    ("Quoted closing delimiter", "<box background=\"a > b\">hello</box>", true, []),
    ("Unclosed escape fails closed", "<escape>\n<SH>\necho NEVER\n</SH>", false, []),
    ("Bad closing scope fails closed", "<box>\n<SH>\necho NEVER\n</SH>\n</row>", false, []),
    ("Early DONE fails closed", "<DONE>\n<SH>\necho NEVER\n</SH>", false, []),
    ("Duplicate DONE fails closed", "<DONE>\n<DONE>", false, []),
    ("Unknown command fails closed", "<UNKNOWN>\necho NEVER\n</UNKNOWN>", false, []),
    ("Raw editor inert", "<CodeBlock language=\"text\">\n<SH>\necho NEVER\n</SH>\n</CodeBlock>", true, []),
    ("Unclosed shell fails closed", "<SH>\necho NEVER", false, []),
    ("Unclosed multiline code span inert", "`\n<SH>\necho NEVER\n</SH>\n<DONE>", true, []),
    ("Unclosed HTML comment inert", "<!--\n<SH>\necho NEVER\n</SH>\n<DONE>", true, []),
    ("Commented shell inert", "<!--\n<SH>\necho NEVER\n</SH>\n-->", true, []),
};
var failures = 0;
foreach (var (name, source, valid, commands) in checks)
{
    var result = new RichDocumentParser().Parse(source);
    var names = result.Commands.Select(c => c.Name).ToArray();
    var pass = result.Document.Valid == valid && names.SequenceEqual(commands);
    if (name == "Shell body and DONE")
        pass &= result.Commands.FirstOrDefault()?.Body == "echo first\r\n<DONE>";
    if (name == "Opaque escape")
        pass &= result.Document.Nodes.Any(n => n.Kind == "literal" && n.Text?.Contains("echo NEVER") == true);
    if (name == "Nested box and text")
        pass &= result.Document.Nodes.Any(n => n.Kind == "element" && n.Name == "box" &&
            n.Children?.Any(c => c.Kind == "element" && c.Name == "text") == true);
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {name}; valid={result.Document.Valid}; commands={string.Join(",", names)}; diagnostics={string.Join(",", result.Document.Diagnostics.Select(d => d.Code))}");
    if (!pass) failures++;
}
var fixture = File.ReadAllText(Path.Combine("tests", "fixtures", "rich-model-ui", "custom-controls-question.md"));
var parsedQuestion = new RichDocumentParser().Parse(fixture);
var question = parsedQuestion.Document.Nodes.SingleOrDefault(n => n.Kind == "question");
var choices = question?.Children?.Where(n => n.Kind == "questionOption").ToArray() ?? [];
var buttons = question?.Children?.Where(n => n.Kind == "questionButton").ToArray() ?? [];
var radioPass = parsedQuestion.Document.Valid && parsedQuestion.Commands.Count == 0 &&
    question?.Attributes?["initial"] == "A" &&
    choices.Length == 4 && buttons.Length == 1 &&
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

var rejectedBox = new RichDocumentParser().Parse(wrappedFixture.Replace("<box border", "<box onClick=\"unsafe()\" border"));
var rejectedBoxPass = !rejectedBox.Document.Valid && rejectedBox.Commands.Count == 0;
Console.WriteLine($"{(rejectedBoxPass ? "PASS" : "FAIL")}: Unsafe radio box wrapper remains inert");
if (!rejectedBoxPass) failures++;

var staticStyle = new RichDocumentParser().Parse("<text textAlign=\"center\" tabularNums>42</text><title textAlign=\"center\" tabularNums>Title</title>");
var staticStylePass = staticStyle.Document.Valid &&
    staticStyle.Document.Nodes.Count(n => n.Kind == "element" &&
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
        r.Document.Nodes[0].Children[0].Text == "Before " &&
        r.Document.Nodes[0].Children[1].Name == "text" &&
        r.Document.Nodes[0].Children[1].Attributes?.GetValueOrDefault("strong") == "true" &&
        r.Document.Nodes[0].Children[1].Children?.Count == 1 &&
        r.Document.Nodes[0].Children[1].Children[0].Kind == "text" &&
        r.Document.Nodes[0].Children[1].Children[0].Text == "inside" &&
        r.Document.Nodes[0].Children[2].Text == " after"),
    ("Emphasis spanning an inline rich element", "**Before <text strong>inside</text> after**", r =>
        r.Document.Valid && r.Document.Nodes.Count == 1 &&
        r.Document.Nodes[0].Kind == "paragraph" &&
        r.Document.Nodes[0].Children?.Count == 1 &&
        r.Document.Nodes[0].Children[0].Kind == "strong" &&
        r.Document.Nodes[0].Children[0].Children?.Any(c => c.Name == "text") == true),
    ("Rich element inside list item", "- Before <text strong>inside</text> after", r =>
        r.Document.Valid && r.Document.Nodes.Count == 1 &&
        r.Document.Nodes[0].Kind == "list" &&
        r.Document.Nodes[0].Children?.Count == 1 &&
        r.Document.Nodes[0].Children[0].Children?.Any(n =>
            n.Kind == "paragraph" && n.Children?.Any(c => c.Name == "text") == true) == true),
    ("Recursive box with inline rich child", "<box>hello <text strong>world</text></box>", r =>
        r.Document.Valid && r.Document.Nodes.Count == 1 &&
        r.Document.Nodes[0].Name == "box" &&
        r.Document.Nodes[0].Children?.Count == 1 &&
        r.Document.Nodes[0].Children[0].Kind == "paragraph" &&
        r.Document.Nodes[0].Children[0].Children?.Any(n => n.Name == "text" &&
            n.Children?.Count == 1 && n.Children[0].Text == "world") == true),
    ("Standalone inline element has no paragraph inside span", "<text strong>world</text>", r =>
        r.Document.Valid && r.Document.Nodes.Count == 1 &&
        r.Document.Nodes[0].Kind == "paragraph" &&
        r.Document.Nodes[0].Children?.Count == 1 &&
        r.Document.Nodes[0].Children[0].Name == "text" &&
        r.Document.Nodes[0].Children[0].Children?.Count == 1 &&
        r.Document.Nodes[0].Children[0].Children[0].Kind == "text"),
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
    ("SVG embedded commands rejected", "<svg viewBox=\"0 0 100 100\"><SH>\necho NEVER\n</SH></svg>", false, r => r.Commands.Count == 0),
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
var nestedScope = "<box>\n~~~md\n</box>\n~~~\n<box><text>Nested sibling</text></box>\n</box>";
var nestedParsed = new RichDocumentParser().Parse(nestedScope);
var nestedPass = nestedParsed.Document.Valid && nestedParsed.Commands.Count == 0 &&
    nestedParsed.Document.Nodes.Any(n => n.Name == "box" &&
        n.Children?.Any(c => c.Name == "box" && c.Children?.Any(x => x.Name == "text") == true) == true);
Console.WriteLine($"{(nestedPass ? "PASS" : "FAIL")}: Fenced false closing delimiter inside nested layout; valid={nestedParsed.Document.Valid}; diagnostics={string.Join("|", nestedParsed.Document.Diagnostics.Select(d => d.Code))}; root={string.Join(",", nestedParsed.Document.Nodes.Select(n => n.Kind + ":" + n.Name))}");
if (!nestedPass) failures++;

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
var deepPass = deepParsed.Document.Valid && deepParsed.Commands.Count == 0 &&
    timer.Elapsed < TimeSpan.FromSeconds(8);
Console.WriteLine($"{(deepPass ? "PASS" : "FAIL")}: Nested protected code bounded in {timer.ElapsedMilliseconds} ms");
if (!deepPass) failures++;

Console.WriteLine($"{checks.Length + svgTests.Length + inlineTests.Length + 6 - failures}/{checks.Length + svgTests.Length + inlineTests.Length + 6} passed");
return failures == 0 ? 0 : 1;
