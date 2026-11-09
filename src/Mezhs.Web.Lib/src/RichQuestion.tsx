import { parse, parseExpressionAt } from "acorn";
import { fromMarkdown } from "mdast-util-from-markdown";
import { unified } from "unified";
import remarkParse from "remark-parse";
import remarkMdx from "remark-mdx";
import { createContext, useContext, useId, useState } from "react";
import Markdown, { uriTransformer } from "react-markdown";
import remarkGfm from "remark-gfm";
import { RichActionContext } from "./RichActionContext";

// This is a deliberately finite grammar, not an MDX/DIL interpreter. Acorn only
// inspects syntax; nothing from the model is ever invoked as JavaScript.
export type RichQuestionData = {
  initial: string;
  options: Array<{ value: string; label: string }>;
  button: string;
  prefix: string;
  suffix: string;
  block?: boolean;
};

const identifier = /^[A-Za-z_$][\w$]*$/;
const forbidden = new Set(["constructor", "prototype", "__proto__", "globalThis"]);
const maxQuestionLength = 8192;

function identifierName(node: any): string | null {
  return node?.type === "Identifier" && identifier.test(node.name) && !forbidden.has(node.name)
    ? node.name : null;
}

function declaration(code: string) {
  if (code.length > 300) return null;
  try {
    const statements: any = parse(code, { ecmaVersion: "latest" });
    if (statements.body.length !== 1) return null;
    const statement = statements.body[0];
    const decl = statement.declarations?.[0];
    const pair = decl?.id;
    const call = decl?.init;
    if (statement.type !== "VariableDeclaration" || statement.kind !== "const" ||
        statement.declarations.length !== 1 || pair?.type !== "ArrayPattern" ||
        pair.elements.length !== 2 || call?.type !== "CallExpression" ||
        call.optional || call.arguments.length !== 1 ||
        call.callee?.type !== "MemberExpression" || call.callee.computed ||
        identifierName(call.callee.object) !== "DIL" ||
        identifierName(call.callee.property) !== "useState") return null;
    const state = identifierName(pair.elements[0]);
    const setter = identifierName(pair.elements[1]);
    const value = call.arguments[0];
    if (!state || !setter || state === setter || value.type !== "Literal" ||
        typeof value.value !== "string" || value.value.length > 64) return null;
    return { state, setter, initial: value.value as string };
  } catch { return null; }
}

function handler(code: string, state: string) {
  if (code.length > 3000) return null;
  try {
    const action: any = parseExpressionAt(code, 0, { ecmaVersion: "latest" });
    if (code.slice(action.end).trim() || action.type !== "ArrowFunctionExpression" ||
        action.async || action.params.length !== 0) return null;
    const expression = action.body.type === "BlockStatement"
      ? action.body.body.length === 1 && action.body.body[0].type === "ExpressionStatement"
        ? action.body.body[0].expression : null : action.body;
    if (expression?.type !== "CallExpression" || expression.optional ||
        expression.arguments.length !== 1 || expression.callee?.type !== "MemberExpression" ||
        expression.callee.computed || identifierName(expression.callee.object) !== "GenUI" ||
        identifierName(expression.callee.property) !== "issueNewTurn") return null;
    const template = expression.arguments[0];
    if (template?.type !== "TemplateLiteral" || template.expressions.length !== 1 ||
        identifierName(template.expressions[0]) !== state || template.quasis.length !== 2) return null;
    const prefix = template.quasis[0].value.cooked;
    const suffix = template.quasis[1].value.cooked;
    if (typeof prefix !== "string" || typeof suffix !== "string" ||
        prefix.length + suffix.length > 2000) return null;
    return { prefix, suffix };
  } catch { return null; }
}

type MarkdownNode = {
  type: string;
  children?: MarkdownNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
};

// CommonMark AST locations establish which source ranges are code examples.
// This covers indented blocks and fences nested in lists/quotes without a
// separate hand-written Markdown scanner. No model code is ever evaluated.
function protectedPositions(source: string): Uint8Array | null {
  try {
    // Model content can mix LF and CRLF (notably after Windows fixture or
    // transport transformations). Normalize only the syntax prepass and map
    // offsets back to the exact original source without rewriting its text.
    let syntax = source;
    let originalOffsets: number[] | undefined;
    if (source.includes("\r")) {
      const chars: string[] = [];
      originalOffsets = [];
      for (let i = 0; i < source.length;) {
        originalOffsets.push(i);
        if (source[i] === "\r") {
          chars.push("\n");
          i += source[i + 1] === "\n" ? 2 : 1;
        } else chars.push(source[i++]);
      }
      originalOffsets.push(source.length);
      syntax = chars.join("");
    }
    const mask = new Uint8Array(source.length);
    const nodes: MarkdownNode[] = [fromMarkdown(syntax) as unknown as MarkdownNode];
    while (nodes.length) {
      const node = nodes.pop()!;
      if (node.type === "code" || node.type === "inlineCode") {
        const from = node.position?.start.offset;
        const to = node.position?.end.offset;
        if (from === undefined || to === undefined || from < 0 ||
            to > syntax.length || to <= from) return null;
        const start = originalOffsets ? originalOffsets[from] : from;
        const end = originalOffsets ? originalOffsets[to] : to;
        mask.fill(1, start, end);
      } else if (node.children) {
        for (let i = node.children.length - 1; i >= 0; i--) nodes.push(node.children[i]);
      }
    }
    // Conservative backup for source fences when container/list parsing
    // yields only separate marker-line code nodes. Never grant authority to
    // something bracketed by a Markdown-style fence, even if AST contexts
    // disagree. The strict closer rule preserves fenced examples with faux
    // closing markers; runtime and allocations remain bounded by source size.
    let offset = 0;
    let fence: { from: number; marker: string } | null = null;
    for (const line of source.match(/[^\n]*(?:\n|$)/g) || []) {
      if (!line) continue;
      if (fence) {
        const closing = /^ {0,3}(`{3,}|~{3,})[ \t]*(?:\r?\n)?$/.exec(line)?.[1];
        if (closing?.[0] === fence.marker[0] && closing.length >= fence.marker.length) {
          mask.fill(1, fence.from, offset + line.length);
          fence = null;
        }
      } else {
        const opening = /^ {0,3}(`{3,}|~{3,})/.exec(line)?.[1];
        if (opening) fence = { from: offset, marker: opening };
      }
      offset += line.length;
    }
    if (fence) mask.fill(1, fence.from);
    return mask;
  } catch {
    // A failed syntax prepass cannot grant action authority.
    return null;
  }
}


type SyntaxNode = {
  type: string;
  name?: string | null;
  attributes?: Array<{ type: string; name?: string; value?: string | { value: string } | null }>;
  children?: SyntaxNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
  value?: string;
};

type BodyDeclaration = { start: number; end: number; state: string; setter: string; initial: string };
type Replacement = { from: number; to: number; text: string; questionId?: string };
const bodyPattern = /\{@body[^\r\n}]{1,320}\}/g;
const jsx = (node: SyntaxNode, name: string) =>
  (node.type === "mdxJsxFlowElement" || node.type === "mdxJsxTextElement") && node.name === name;

function boundaries(node: SyntaxNode): { start: number; end: number } | null {
  const start = node.position?.start.offset;
  const end = node.position?.end.offset;
  return typeof start === "number" && typeof end === "number" && start >= 0 && end > start
    ? { start, end } : null;
}

function attributeMap(node: SyntaxNode) {
  const values = new Map<string, string | { value: string } | null>();
  for (const attr of node.attributes || []) {
    if (attr.type !== "mdxJsxAttribute" || !attr.name || values.has(attr.name)) return null;
    if (attr.value !== null && typeof attr.value !== "string" &&
        (typeof attr.value !== "object" || typeof attr.value.value !== "string")) return null;
    values.set(attr.name, attr.value ?? null);
  }
  return values;
}

function expressionAttribute(value: unknown, identifierValue: string) {
  return !!value && typeof value === "object" &&
    (value as { value?: unknown }).value?.toString() === identifierValue;
}

// Markdown syntax positions, rather than textual tag matching, determine both
// whether a directive is code and whether a group/button are structural siblings.
// Acorn validates the only model-authored JS forms accepted as inert syntax data.
export function extractRichQuestions(source: string): {
  content: string;
  questions: Record<string, RichQuestionData>;
  slotOffsets: Record<string, number>;
} {
  const questions: Record<string, RichQuestionData> = Object.create(null);
  const slotOffsets: Record<string, number> = Object.create(null);
  const empty = () => ({ content: source, questions, slotOffsets });
  if (!source.includes("{@body") || source.length > 180000) return empty();
  const protectedMask = protectedPositions(source);
  if (!protectedMask) return empty();
  const protectedAt = (index: number) => protectedMask[index] === 1;
  const escapedAt = (text: string, index: number) => {
    let backslashes = 0;
    for (let i = index - 1; i >= 0 && text[i] === "\\"; i--) backslashes++;
    return backslashes % 2 === 1;
  };

  const masked = source.split("");
  const declarations: BodyDeclaration[] = [];
  for (const match of source.matchAll(bodyPattern)) {
    const start = match.index!;
    const end = start + match[0].length;
    if (protectedAt(start) || escapedAt(source, start)) continue;
    // A directive is only authorized when it occupies its own Markdown line,
    // never within a quote, list marker, inline text, attribute or HTML comment.
    const lineStart = source.lastIndexOf("\n", start - 1) + 1;
    const newline = source.indexOf("\n", end);
    const lineEnd = newline < 0 ? source.length : newline;
    const standalone = !source.slice(lineStart, start).trim() &&
      !source.slice(end, lineEnd).trim();
    if (!standalone) continue;
    // MDX cannot parse {@body}; preserving byte offsets lets AST positions
    // refer to the original source while declarations remain non-executable.
    for (let i = start; i < end; i++) masked[i] = " ";
    const parsed = declaration(match[0].slice(6, -1).trim());
    if (parsed && declarations.length < 16)
      declarations.push({ start, end, ...parsed });
  }
  if (!declarations.length) return empty();

  let tree: SyntaxNode;
  try {
    tree = unified().use(remarkParse).use(remarkGfm).use(remarkMdx)
      .parse(masked.join("")) as SyntaxNode;
  } catch {
    // Never reconstruct actionable controls from context-free recovery blocks.
    return escapeUnsupported(source, [], questions, slotOffsets, protectedMask);
  }

  let seenNodes = 0;
  const groups: Array<{ node: SyntaxNode; parent: SyntaxNode; index: number }> = [];
  function visit(node: SyntaxNode, depth: number): boolean {
    if (++seenNodes > 12000 || depth > 40) return false;
    for (const [index, child] of (node.children || []).entries()) {
      if (jsx(child, "radio-group")) groups.push({ node: child, parent: node, index });
      if (!visit(child, depth + 1)) return false;
    }
    return true;
  }
  if (!visit(tree, 0)) return escapeUnsupported(source, [], questions, slotOffsets, protectedMask);

  const usedDeclarations = new Set<number>();
  const replacements: Replacement[] = [];
  for (const entry of groups) {
    if (Object.keys(questions).length >= 16) break;
    const { node: group, parent, index } = entry;
    const groupPos = boundaries(group);
    if (!groupPos || groupPos.end - groupPos.start > 8192) continue;
    // Only a root question or a question whose first child is inside one
    // static DIL box is in scope. No arbitrary nesting or adjacent buttons.
    const wrapped = jsx(parent, "box");
    if ((!wrapped && parent.type !== "root") || (wrapped && index !== 0)) continue;
    const parentPos = wrapped ? boundaries(parent) : null;
    if (wrapped && !parentPos) continue;
    const sibling = parent.children?.[index + 1];
    const button = sibling?.type === "paragraph" && sibling.children?.length === 1
      ? sibling.children[0]
      : sibling;
    if (!button || !jsx(button, "button")) continue;
    const buttonPos = boundaries(button);
    if (!buttonPos || buttonPos.start < groupPos.end ||
        !/^\s*$/.test(source.slice(groupPos.end, buttonPos.start))) continue;

    const attrs = attributeMap(group);
    if (!attrs || (attrs.size !== 2 && attrs.size !== 3) ||
        (attrs.size === 3 && attrs.get("direction") !== "col") ||
        (attrs.size === 2 && attrs.has("direction")) ||
        !attrs.has("value") || !attrs.has("onChange")) continue;
    const radioNodes = group.children || [];
    if (radioNodes.length < 2 || radioNodes.length > 12) continue;

    const candidate = [...declarations].reverse().find(item =>
      !usedDeclarations.has(item.start) &&
      item.end < groupPos.start && groupPos.start - item.start <= 8192);
    if (!candidate ||
        !expressionAttribute(attrs.get("value"), candidate.state) ||
        !expressionAttribute(attrs.get("onChange"), candidate.setter)) continue;

    // The permitted gap contains only whitespace or the opening of the
    // containing box. A separate paragraph/element cannot lend its state.
    if (wrapped) {
      const boxStart = parentPos!.start;
      if (boxStart < candidate.end ||
          source.slice(candidate.end, boxStart).trim() ||
          !/^<box\b[^>]{0,512}>\s*$/.test(source.slice(boxStart, groupPos.start)))
        continue;
    } else if (source.slice(candidate.end, groupPos.start).trim()) continue;

    const selected = new Set<string>();
    const options: RichQuestionData["options"] = [];
    let validOptions = true;
    for (const radio of radioNodes) {
      if (!jsx(radio, "radio")) { validOptions = false; break; }
      const values = attributeMap(radio);
      const value = values?.get("value");
      const children = radio.children || [];
      if (!values || values.size !== 1 || typeof value !== "string" ||
          !/^[A-Za-z0-9_-]{1,64}$/.test(value) || selected.has(value) ||
          !children.length || children.some(child => !safeLabelNode(child))) {
        validOptions = false; break;
      }
      const first = boundaries(children[0]);
      const last = boundaries(children[children.length - 1]);
      if (!first || !last) { validOptions = false; break; }
      const label = source.slice(first.start, last.end).trim();
      if (!label || label.length > 600) { validOptions = false; break; }
      selected.add(value);
      options.push({ value, label });
    }
    if (!validOptions || !selected.has(candidate.initial)) continue;

    const buttonAttributes = attributeMap(button);
    if (!buttonAttributes || !buttonAttributes.has("onClick") ||
        (buttonAttributes.size !== 1 && buttonAttributes.size !== 2) ||
        (buttonAttributes.size === 2 && buttonAttributes.get("block") !== null) ||
        (buttonAttributes.size === 2 && !buttonAttributes.has("block"))) continue;
    const click = buttonAttributes.get("onClick");
    if (!click || typeof click !== "object") continue;
    const action = handler(click.value, candidate.state);
    const buttonChildren = button.children || [];
    if (!action || buttonChildren.length !== 1 || buttonChildren[0].type !== "text") continue;
    const buttonText = buttonChildren[0].value?.trim() || "";
    if (!buttonText || buttonText.length > 80) continue;

    const id = String(Object.keys(questions).length);
    questions[id] = {
      initial: candidate.initial, options, button: buttonText,
      block: buttonAttributes.has("block"), ...action,
    };
    usedDeclarations.add(candidate.start);
    replacements.push(
      { from: candidate.start, to: candidate.end, text: "" },
      { from: groupPos.start, to: groupPos.end,
        text: '<mezhs-question-slot id="' + id + '" />', questionId: id },
      { from: buttonPos.start, to: buttonPos.end, text: "" },
    );
  }
  return escapeUnsupported(source, replacements, questions, slotOffsets, protectedMask);
}

function safeLabelNode(node: SyntaxNode): boolean {
  if (node.type.startsWith("mdx") || node.type === "html" ||
      node.type === "definition" || node.type === "image") return false;
  return (node.children || []).every(safeLabelNode);
}

function escapeUnsupported(
  source: string, replacements: Replacement[],
  questions: Record<string, RichQuestionData>, slotOffsets: Record<string, number>,
  originalMask: Uint8Array,
) {
  let output = "";
  let cursor = 0;
  for (const replacement of replacements.sort((a, b) => a.from - b.from)) {
    if (replacement.from < cursor) continue;
    output += source.slice(cursor, replacement.from);
    if (replacement.questionId !== undefined)
      slotOffsets[replacement.questionId] = output.length;
    output += replacement.text;
    cursor = replacement.to;
  }
  output += source.slice(cursor);
  if (!output.includes("{@body")) return { content: output, questions, slotOffsets };
  const mask = replacements.length ? protectedPositions(output) : originalMask;
  if (!mask) return { content: source, questions: Object.create(null),
    slotOffsets: Object.create(null) };
  const additions: number[] = [];
  let result = "";
  let from = 0;
  for (const match of output.matchAll(/\{@body/g)) {
    const index = match.index!;
    if (mask[index]) continue;
    let slashes = 0;
    for (let i = index - 1; i >= 0 && output[i] === "\\"; i--) slashes++;
    if (slashes % 2) continue;
    result += output.slice(from, index) + "\\";
    from = index;
    additions.push(index);
  }
  result += output.slice(from);
  for (const id of Object.keys(slotOffsets))
    slotOffsets[id] += additions.filter(offset => offset < slotOffsets[id]).length;
  return { content: result, questions, slotOffsets };
}

export function RichQuestion({ data }: { data: RichQuestionData }) {
  const onIssueNewTurn = useContext(RichActionContext);
  const groupName = useId();
  const [choice, setChoice] = useState(data.initial);
  const active = data.options.some(option => option.value === choice);
  const enabled = !!onIssueNewTurn && active;
  return <fieldset className="rich-question">
    <legend className="rich-question-caption">Choose one option</legend>
    {data.options.map(option => <label key={option.value} className="rich-question-option">
      <input type="radio" name={groupName} value={option.value}
        checked={choice === option.value} onChange={() => setChoice(option.value)} />
      <div><Markdown remarkPlugins={[remarkGfm]}
        transformLinkUri={uriTransformer} transformImageUri={() => ""}>{option.label}</Markdown></div>
    </label>)}
    <button type="button" className={data.block ? "rich-question-block" : undefined} disabled={!enabled} onClick={() => {
      if (enabled) onIssueNewTurn?.(data.prefix + choice + data.suffix);
    }}>{data.button}</button>
    {!onIssueNewTurn && <small className="rich-question-readonly">Submission unavailable in this view.</small>}
  </fieldset>;
}

export const RichQuestionsContext = createContext<Record<string, RichQuestionData>>(Object.create(null));

export function RichQuestionSlot(props: Record<string, unknown>) {
  const questions = useContext(RichQuestionsContext);
  const id = props["data-mezhs-question-id"];
  const data = typeof id === "string" && Object.prototype.hasOwnProperty.call(questions, id)
    ? questions[id] : undefined;
  return data ? <RichQuestion data={data} /> : null;
}
