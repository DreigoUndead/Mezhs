import { parse, parseExpressionAt } from "acorn";
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

// An opening tag scanner is necessary because an arrow's => is not the end
// of <button onClick={...}>. Quotes and JavaScript braces stay inert here.
function tagEnd(source: string, start: number) {
  let braces = 0;
  let quote = "";
  for (let i = start; i < source.length && i < start + 3500; i++) {
    const char = source[i];
    if (quote) {
      if (char === "\\") { i++; continue; }
      if (char === quote) quote = "";
    } else if (char === "'" || char === '"' || char === "`") {
      quote = char;
    } else if (char === "{") braces++;
    else if (char === "}") { if (--braces < 0) return -1; }
    else if (char === ">" && braces === 0) return i;
  }
  return -1;
}

function bindings(source: string, state: string, setter: string) {
  const attribute = /([A-Za-z]+)\s*=\s*\{\s*([A-Za-z_$][\w$]*)\s*\}/g;
  const found = [...source.matchAll(attribute)];
  if (found.length !== 2 || source.replace(attribute, "").trim()) return false;
  const values = new Map(found.map(item => [item[1], item[2]]));
  return values.size === 2 && values.get("value") === state && values.get("onChange") === setter;
}

function options(source: string, initial: string) {
  const option = /<radio\s+value\s*=\s*(["'])([A-Za-z0-9_-]{1,64})\1\s*>([\s\S]*?)<\/radio\s*>/g;
  const matches = [...source.matchAll(option)];
  if (matches.length < 2 || matches.length > 12 || source.replace(option, "").trim()) return null;
  const seen = new Set<string>();
  const values = [];
  for (const item of matches) {
    const label = item[3].trim();
    if (!label || label.length > 300 || /[<>{}]/.test(label) || seen.has(item[2])) return null;
    seen.add(item[2]);
    values.push({ value: item[2], label });
  }
  return seen.has(initial) ? values : null;
}

// Only code fences and actual inline Markdown code are shielded. A real body
// directive never gains authority merely because it resembles a code example.
function protectedPositions(source: string) {
  // Each byte indicates whether a character belongs to fenced or inline code.
  // Membership checks and marking disjoint ranges are linear in source length.
  const mask = new Uint8Array(source.length);
  let offset = 0;
  let fence: { start: number; char: string; length: number } | null = null;
  for (const line of source.match(/[^\n]*(?:\n|$)/g) || []) {
    if (!line) continue;
    const marker = /^ {0,3}(`{3,}|~{3,})/.exec(line)?.[1];
    if (fence) {
      // A valid closing fence occupies the whole line, except whitespace.
      const closing = /^ {0,3}(`{3,}|~{3,})[ \t]*(?:\r?\n)?$/.exec(line)?.[1];
      if (closing?.[0] === fence.char && closing.length >= fence.length) {
        mask.fill(1, fence.start, offset + line.length);
        fence = null;
      }
    } else if (marker) fence = { start: offset, char: marker[0], length: marker.length };
    offset += line.length;
  }
  if (fence) mask.fill(1, fence.start);

  // Find exact closing backtick runs with one reverse pass. Repeated searches
  // for a missing closer can also become quadratic on malformed model text.
  const ticks = [...source.matchAll(/`+/g)].filter(match => !mask[match.index!]);
  const following = new Array<number>(ticks.length).fill(-1);
  const nextByLength = new Map<number, number>();
  for (let i = ticks.length - 1; i >= 0; i--) {
    const length = ticks[i][0].length;
    following[i] = nextByLength.get(length) ?? -1;
    nextByLength.set(length, i);
  }
  for (let i = 0; i < ticks.length; i++) {
    const start = ticks[i].index!;
    if (mask[start]) continue;
    // Shield matching Markdown backtick spans regardless of nearby JSX-like text.
    // Exempting a run because of an earlier "onClick={" can activate literal
    // {@body ...} examples. A real button handler is validated separately.
    const close = following[i];
    if (close < 0) continue;
    mask.fill(1, start, ticks[close].index! + ticks[i][0].length);
    i = close;
  }
  return mask;
}

export function extractRichQuestions(source: string): {
  content: string;
  questions: Record<string, RichQuestionData>;
  slotOffsets: Record<string, number>;
} {
  const questions: Record<string, RichQuestionData> = Object.create(null);
  if (!source.includes("{@body") || source.length > 180000)
    return { content: source, questions, slotOffsets: Object.create(null) as Record<string, number> };
  const protectedMask = protectedPositions(source);
  const protectedAt = (position: number) => protectedMask[position] === 1;
  const escapedAt = (position: number) => {
    let slashes = 0;
    for (let i = position - 1; i >= 0 && source[i] === "\\"; i--) slashes++;
    return slashes % 2 === 1;
  };
  const replacements: Array<{ from: number; to: number; text: string; questionId?: string }> = [];
  let cursor = 0;
  while (Object.keys(questions).length < 16) {
    const start = source.indexOf("{@body", cursor);
    if (start < 0) break;
    cursor = start + 6;
    if (protectedAt(start) || escapedAt(start)) continue;
    const bodyEnd = source.indexOf("}", cursor);
    if (bodyEnd < 0 || bodyEnd - start > 320) continue;
    const state = declaration(source.slice(start + 6, bodyEnd).trim());
    if (!state) continue;
    const maxEnd = Math.min(source.length, bodyEnd + maxQuestionLength);
    const groupStart = source.indexOf("<radio-group", bodyEnd + 1);
    if (groupStart < 0 || groupStart >= maxEnd ||
        source.slice(bodyEnd + 1, groupStart).includes("{@body") || protectedAt(groupStart) || escapedAt(groupStart)) continue;
    const groupOpenEnd = tagEnd(source, groupStart);
    if (groupOpenEnd < 0 || groupOpenEnd >= maxEnd ||
        !bindings(source.slice(groupStart + "<radio-group".length, groupOpenEnd), state.state, state.setter)) continue;
    const groupClose = source.indexOf("</radio-group>", groupOpenEnd + 1);
    if (groupClose < 0 || groupClose >= maxEnd) continue;
    const choices = options(source.slice(groupOpenEnd + 1, groupClose), state.initial);
    if (!choices) continue;
    const buttonStart = source.indexOf("<button", groupClose + "</radio-group>".length);
    if (buttonStart < 0 || buttonStart >= maxEnd ||
        source.slice(groupClose, buttonStart).includes("{@body") || protectedAt(buttonStart) || escapedAt(buttonStart)) continue;
    const buttonOpenEnd = tagEnd(source, buttonStart);
    if (buttonOpenEnd < 0 || buttonOpenEnd >= maxEnd) continue;
    const attrs = source.slice(buttonStart + "<button".length, buttonOpenEnd);
    const click = /^\s*onClick\s*=\s*\{([\s\S]*)\}\s*$/.exec(attrs);
    const action = click && handler(click[1], state.state);
    if (!action) continue;
    const buttonClose = source.indexOf("</button>", buttonOpenEnd + 1);
    if (buttonClose < 0 || buttonClose >= maxEnd) continue;
    const buttonText = source.slice(buttonOpenEnd + 1, buttonClose).trim();
    if (!buttonText || buttonText.length > 80 || /[<>{}]/.test(buttonText)) continue;
    const id = String(Object.keys(questions).length);
    questions[id] = { initial: state.initial, options: choices, button: buttonText, ...action };
    replacements.push(
      { from: start, to: bodyEnd + 1, text: "" },
      { from: groupStart, to: groupClose + "</radio-group>".length,
        text: '<mezhs-question-slot id="' + id + '" />', questionId: id },
      { from: buttonStart, to: buttonClose + "</button>".length, text: "" },
    );
    cursor = buttonClose + "</button>".length;
  }
  // Unsupported body directives must not poison the MDX parser. Escaping the
  // opening brace leaves the original directive legible but always inert.
  let output = "";
  let pos = 0;
  // Only positions inserted by this extractor are allowed to become actions.
  // A model-authored <mezhs-question-slot> with the same ID is still inert.
  const slotOffsets: Record<string, number> = Object.create(null);
  for (const replacement of replacements.sort((a, b) => a.from - b.from)) {
    output += source.slice(pos, replacement.from);
    if (replacement.questionId !== undefined)
      slotOffsets[replacement.questionId] = output.length;
    output += replacement.text;
    pos = replacement.to;
  }
  output += source.slice(pos);
  const escapedMask = protectedPositions(output);
  const escapeInsertions: number[] = [];
  output = output.replace(/\{@body/g, (match, index: number) => {
    if (escapedMask[index]) return match;
    escapeInsertions.push(index);
    return "\\{" + match.slice(1);
  });
  // The extra Markdown escapes shift downstream AST source positions.
  for (const id of Object.keys(slotOffsets)) {
    const original = slotOffsets[id];
    slotOffsets[id] = original + escapeInsertions.filter(offset => offset < original).length;
  }
  return { content: output, questions, slotOffsets };
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
    <button type="button" disabled={!enabled} onClick={() => {
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
