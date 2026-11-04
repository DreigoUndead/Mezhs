import { parseExpressionAt } from "acorn";
import JSON5 from "json5";

type Expr = {
  type: string;
  end: number;
  value?: unknown;
  name?: string;
  operator?: string;
  object?: Expr;
  property?: Expr;
  computed?: boolean;
  argument?: Expr;
  left?: Expr;
  right?: Expr;
  test?: Expr;
  consequent?: Expr;
  alternate?: Expr;
  expressions?: Expr[];
  quasis?: Array<{ value: { cooked: string | null } }>;
  elements?: Array<Expr | null>;
  properties?: Array<{ type: string; kind?: string; computed?: boolean; key: Expr; value: Expr }>;
};

const forbiddenKeys = new Set(["__proto__", "prototype", "constructor"]);
const maxIterations = 256;
const maxItems = 40;
const maxOutput = 180000;

function evaluate(node: Expr, scope: Record<string, unknown>, depth = 0): unknown {
  if (depth > 16) throw Error("Expression too deep");
  const next = (value?: Expr) => {
    if (!value) throw Error("Incomplete expression");
    return evaluate(value, scope, depth + 1);
  };
  switch (node.type) {
    case "Literal":
      if (node.value === null || ["string", "number", "boolean"].includes(typeof node.value))
        return node.value;
      break;
    case "Identifier":
      if (node.name && !forbiddenKeys.has(node.name) &&
          Object.prototype.hasOwnProperty.call(scope, node.name))
        return scope[node.name];
      break;
    case "MemberExpression": {
      if (node.computed || node.property?.type !== "Identifier") break;
      const name = node.property.name;
      const object = next(node.object);
      if (name && !forbiddenKeys.has(name) && object && typeof object === "object" &&
          Object.prototype.hasOwnProperty.call(object, name))
        return (object as Record<string, unknown>)[name];
      break;
    }
    case "UnaryExpression": {
      const value = next(node.argument);
      if (node.operator === "!" && typeof value === "boolean") return !value;
      if (node.operator === "-" && typeof value === "number") return -value;
      if (node.operator === "+" && typeof value === "number") return value;
      break;
    }
    case "BinaryExpression": {
      const left = next(node.left);
      const right = next(node.right);
      if (node.operator === "===") return left === right;
      if (node.operator === "!==") return left !== right;
      if (node.operator === "+" && (typeof left === "string" || typeof right === "string"))
        return String(left) + String(right);
      if (typeof left !== "number" || typeof right !== "number") break;
      let result: number;
      switch (node.operator) {
        case "+": result = left + right; break;
        case "-": result = left - right; break;
        case "*": result = left * right; break;
        case "/": result = left / right; break;
        case "%": result = left % right; break;
        default: throw Error("Unsupported operation");
      }
      if (!Number.isFinite(result) || Math.abs(result) > 1000000) break;
      return result;
    }
    case "ConditionalExpression": {
      const condition = next(node.test);
      if (typeof condition !== "boolean") break;
      return next(condition ? node.consequent : node.alternate);
    }
    case "TemplateLiteral": {
      if (!node.quasis || !node.expressions || node.quasis.length !== node.expressions.length + 1)
        break;
      let result = node.quasis[0].value.cooked || "";
      for (let i = 0; i < node.expressions.length; i++) {
        const value = next(node.expressions[i]);
        if (!["string", "number", "boolean"].includes(typeof value)) throw Error("Unsafe template interpolation");
        result += String(value) + (node.quasis[i + 1].value.cooked || "");
      }
      if (result.length <= 2048) return result;
      break;
    }
    case "ArrayExpression":
      if (!node.elements || node.elements.length > 48 || node.elements.some(item => item === null))
        break;
      return node.elements.map(item => next(item || undefined));
    case "ObjectExpression": {
      if ((node.properties || []).length > 48) throw Error("Object too large");
      const obj: Record<string, unknown> = Object.create(null);
      for (const property of node.properties || []) {
        if (property.type !== "Property" || property.kind !== "init" || property.computed)
          throw Error("Unsupported object property");
        const key = property.key.type === "Identifier" ? property.key.name : property.key.value;
        if (typeof key !== "string" || forbiddenKeys.has(key)) throw Error("Unsafe object key");
        obj[key] = next(property.value);
      }
      return obj;
    }
  }
  throw Error("Unsupported expression: " + node.type);
}

function parseValue(source: string, scope: Record<string, unknown>, maxLength = 512) {
  if (source.length > maxLength) throw Error("Expression too long");
  const node = parseExpressionAt(source, 0, { ecmaVersion: "latest" }) as unknown as Expr;
  if (source.slice(node.end).trim()) throw Error("Extra expression tokens");
  return evaluate(node, scope);
}

function textValue(value: unknown) {
  if (!["string", "number", "boolean"].includes(typeof value)) throw Error("Non-scalar text");
  return String(value).replace(/&/g, "&amp;").replace(/</g, "&lt;")
    .replace(/>/g, "&gt;").replace(/\\/g, "&#92;");
}

function interpolateText(source: string, scope: Record<string, unknown>) {
  return source.replace(/\{([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\}/g, (match, path: string) => {
    try { return textValue(parseValue(path, scope)); }
    catch { return match; }
  });
}

function interpolateSegment(source: string, scope: Record<string, unknown>) {
  // Process JSX attribute braces before inline field interpolation, preserving
  // expression boundaries including template literals and nested objects.
  let result = "";
  let cursor = 0;
  const prop = /\b[\w-]+\s*=\s*\{/g;
  let match: RegExpExecArray | null;
  while ((match = prop.exec(source))) {
    const open = match.index + match[0].length - 1;
    try {
      const node = parseExpressionAt(source, open + 1, { ecmaVersion: "latest" }) as unknown as Expr;
      let close = node.end;
      while (/\s/.test(source[close] || "") && close < source.length) close++;
      if (source[close] !== "}") continue;
      const expression = source.slice(open + 1, node.end);
      const attributeName = match[0].slice(0, match[0].indexOf("=")).trim();
      const value = parseValue(expression, scope, attributeName === "content" ? 8192 : 512);
      const encoded = value !== null && typeof value === "object"
        ? "json:" + encodeURIComponent(JSON.stringify(value))
        : String(value);
      const escaped = encoded.replace(/&/g, "&amp;").replace(/"/g, "&quot;")
        .replace(/</g, "&lt;").replace(/>/g, "&gt;");
      result += interpolateText(source.slice(cursor, open), scope) + '"' + escaped + '"';
      cursor = close + 1;
      prop.lastIndex = cursor;
    } catch { /* A disallowed expression remains literal and inert. */ }
  }
  return result + interpolateText(source.slice(cursor), scope);
}

function parseEach(source: string, index: number) {
  let start = index + "{#each".length;
  while (/\s/.test(source[start] || "")) start++;
  if (source[start] !== "[") return null;
  let depth = 0;
  let quote = "";
  for (let i = start; i < source.length; i++) {
    const char = source[i];
    if (quote) {
      if (char === "\\") { i++; continue; }
      if (char === quote) quote = "";
    } else if (char === "'" || char === '"') {
      quote = char;
    } else if (char === "[") {
      depth++;
    } else if (char === "]" && --depth === 0) {
      const after = /^\s+as\s+([A-Za-z_$][\w$]*)(?:\s*,\s*([A-Za-z_$][\w$]*))?\s*\}/
        .exec(source.slice(i + 1));
      if (!after || forbiddenKeys.has(after[1]) || (after[2] && forbiddenKeys.has(after[2])))
        return null;
      return {
        literal: source.slice(start, i + 1), name: after[1], index: after[2],
        end: i + 1 + after[0].length,
      };
    }
  }
  return null;
}

function closingEach(source: string, from: number) {
  let nesting = 1;
  let cursor = from;
  while (cursor < source.length) {
    const next = source.indexOf("{#each", cursor);
    const close = source.indexOf("{/each}", cursor);
    if (close < 0) return null;
    if (next >= 0 && next < close) { nesting++; cursor = next + 6; }
    else if (--nesting === 0) return { start: close, end: close + 7 };
    else cursor = close + 7;
  }
  return null;
}

function expand(source: string, scope: Record<string, unknown>, depth: number, budget: { count: number }): string {
  if (depth > 4) throw Error("Too many nested loops");
  let result = "";
  let cursor = 0;
  while (true) {
    const start = source.indexOf("{#each", cursor);
    if (start < 0) break;
    const opening = parseEach(source, start);
    const closing = opening && closingEach(source, opening.end);
    if (!opening || !closing) throw Error("Invalid loop");
    const data: unknown = JSON5.parse(opening.literal);
    if (!Array.isArray(data) || data.length > maxItems) throw Error("Invalid loop data");
    result += interpolateSegment(source.slice(cursor, start), scope);
    const body = source.slice(opening.end, closing.start);
    for (let i = 0; i < data.length; i++) {
      if (++budget.count > maxIterations) throw Error("Too many iterations");
      const child = Object.assign(Object.create(null), scope);
      child[opening.name] = data[i];
      if (opening.index) child[opening.index] = i;
      result += expand(body, child, depth + 1, budget);
      if (result.length > maxOutput) throw Error("Expanded markup too large");
    }
    cursor = closing.end;
  }
  result += interpolateSegment(source.slice(cursor), scope);
  if (result.length > maxOutput) throw Error("Expanded markup too large");
  return result;
}

function shieldFences(source: string) {
  const fences: string[] = [];
  let result = "";
  let fence = "";
  let block = "";
  for (const line of source.match(/[^\n]*\n|[^\n]+$/g) || []) {
    const marker = /^ {0,3}(\x60{3,}|~{3,})/.exec(line)?.[1];
    if (!fence) {
      if (!marker) { result += line; continue; }
      fence = marker;
      block = line;
    } else {
      block += line;
      if (marker && marker[0] === fence[0] && marker.length >= fence.length &&
          /^\s*$/.test(line.slice(line.indexOf(marker) + marker.length))) {
        result += "\uE000" + fences.length + "\uE001";
        fences.push(block);
        block = "";
        fence = "";
      }
    }
  }
  if (fence) {
    result += "\uE000" + fences.length + "\uE001";
    fences.push(block);
  }
  return { result, fences };
}

// Markdown backtick spans are literal examples, not template instructions.
// The lightweight scanner leaves JSX template literals (width={\`...\`})
// alone so their restricted expressions can still be evaluated.
function shieldInlineCode(source: string, protectedBlocks: string[]) {
  let result = "";
  let cursor = 0;
  while (cursor < source.length) {
    const open = source.indexOf("\`", cursor);
    if (open < 0) break;
    let count = 1;
    while (source[open + count] === "\`") count++;
    let search = open + count;
    let close = -1;
    while (search < source.length) {
      const candidate = source.indexOf("\`", search);
      if (candidate < 0) break;
      let run = 1;
      while (source[candidate + run] === "\`") run++;
      if (run === count) { close = candidate; break; }
      search = candidate + run;
    }
    if (close < 0) {
      result += source.slice(cursor, open + count);
      cursor = open + count;
      continue;
    }
    const end = close + count;
    if (/=\s*\{$/.test(source.slice(Math.max(0, open - 32), open))) {
      result += source.slice(cursor, end);
    } else {
      result += source.slice(cursor, open) + "\uE000" + protectedBlocks.length + "\uE001";
      protectedBlocks.push(source.slice(open, end));
    }
    cursor = end;
  }
  return result + source.slice(cursor);
}

export function expandRichTemplates(content: string) {
  if (!content.includes("{#each") && !/\b(?:border|padding|width|content)\s*=\s*\{/.test(content)) return content;
  const { result, fences } = shieldFences(content);
  const shielded = shieldInlineCode(result, fences);
  try {
    return expand(shielded, Object.create(null), 0, { count: 0 })
      .replace(/\uE000(\d+)\uE001/g, (_, n: string) => fences[Number(n)] || "");
  } catch {
    return content;
  }
}
