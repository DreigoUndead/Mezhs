import { createElement } from "react";
import type { ReactNode } from "react";

// SVG is a finite embedded language owned by this trusted component. C# sends
// only an opaque raw body plus bounded root attributes. The decoder runs in SSR
// and the browser: no DOMParser, innerHTML, remote references or model JSX.
type SvgNode = { tag: string; props: Record<string, string>; children: Array<SvgNode | string> };

const graphics = new Set(["g", "rect", "path", "line", "polyline", "polygon", "circle", "ellipse", "text"]);
const elements = new Set([...graphics, "tspan"]);
const leaf = new Set(["rect", "path", "line", "polyline", "polygon", "circle", "ellipse"]);
const common = new Set([
  "fill", "stroke", "strokeWidth", "strokeLinecap", "strokeLinejoin",
  "strokeDasharray", "opacity", "fillOpacity", "strokeOpacity", "transform",
  "fontSize", "fontFamily", "fontWeight", "textAnchor", "dominantBaseline",
]);
const specific: Record<string, ReadonlySet<string>> = {
  svg: new Set(["viewBox", "width", "height", "preserveAspectRatio", "role", "aria-label"]),
  g: new Set(),
  rect: new Set(["x", "y", "rx", "ry", "width", "height"]),
  path: new Set(["d"]),
  line: new Set(["x1", "y1", "x2", "y2"]),
  polyline: new Set(["points"]),
  polygon: new Set(["points"]),
  circle: new Set(["cx", "cy", "r"]),
  ellipse: new Set(["cx", "cy", "rx", "ry"]),
  text: new Set(["x", "y"]),
  tspan: new Set(["x", "y"]),
};
const number = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i;

function numbers(value: string, min: number, max: number, count?: number): boolean {
  const parts = value.trim().split(/[\s,]+/);
  return parts.length > 0 && parts.length <= 200 &&
    (count === undefined || parts.length === count) &&
    parts.every(part => number.test(part) && Number.isFinite(Number(part)) &&
      Number(part) >= min && Number(part) <= max);
}

function validAttribute(tag: string, name: string, value: string): boolean {
  if (!(common.has(name) || specific[tag]?.has(name)) || value.length > 2048 ||
      /[<>\\;]/.test(value) || /url\s*\(|(?:javascript|data)\s*:/i.test(value))
    return false;
  switch (name) {
    case "role": return tag === "svg" && value === "img";
    case "aria-label": return tag === "svg" && value.trim().length > 0 && value.length <= 200;
    case "viewBox": {
      const values = value.trim().split(/[\s,]+/);
      return numbers(value, -10000, 10000, 4) &&
        Number(values[2]) > 0 && Number(values[3]) > 0;
    }
    case "preserveAspectRatio":
      return /^(?:none|x(?:Min|Mid|Max)Y(?:Min|Mid|Max) (?:meet|slice))$/.test(value);
    case "d": {
      if (tag !== "path" || value.length > 1024 ||
          !/^[MmLlHhVvCcSsQqTtAaZz\d+.,\s\-eE]+$/.test(value)) return false;
      const values = value.match(/[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?/gi) || [];
      return values.every(v => Number.isFinite(Number(v)) && Math.abs(Number(v)) <= 10000);
    }
    case "points":
      return (tag === "polygon" || tag === "polyline") &&
        /^[\d+.,\s\-eE]+$/.test(value) && numbers(value, -10000, 10000);
    case "fill":
    case "stroke":
      return /^(?:none|currentColor|transparent|#[0-9a-fA-F]{3,8}|[a-zA-Z]{1,24}|rgba?\([\d.,%\s]+\))$/.test(value);
    case "strokeLinecap": return ["round", "square", "butt"].includes(value);
    case "strokeLinejoin": return ["round", "bevel", "miter"].includes(value);
    case "strokeDasharray": return numbers(value, 0, 200);
    case "textAnchor": return ["start", "middle", "end"].includes(value);
    case "dominantBaseline":
      return ["auto", "middle", "central", "hanging", "alphabetic"].includes(value);
    case "fontFamily": return /^[\w ,'-]{1,64}$/.test(value);
    case "fontWeight": return /^(?:normal|bold|[1-9]00)$/.test(value);
    case "transform": return /^(?:(?:translate|scale|rotate)\([\d+.,\s\-]+\)\s*){1,3}$/.test(value);
    case "opacity":
    case "fillOpacity":
    case "strokeOpacity": return numbers(value, 0, 1, 1);
    case "fontSize": return numbers(value, 1, 80, 1);
    case "strokeWidth": return numbers(value, 0, 30, 1);
    case "width":
      return tag === "svg" ? value === "100%" || numbers(value, 1, 1600, 1) :
        numbers(value, -10000, 10000, 1);
    case "height":
      return tag === "svg" ? numbers(value, 1, 800, 1) : numbers(value, -10000, 10000, 1);
    case "x": case "y": case "x1": case "x2": case "y1": case "y2":
    case "cx": case "cy": case "r": case "rx": case "ry":
      return numbers(value, -10000, 10000, 1);
    default: return false;
  }
}

// XML's five predefined entities and numeric Unicode scalars only.
// Decoded text is React text, never reparsed as tags.
function decode(value: string): string | null {
  let result = "";
  for (let i = 0; i < value.length;) {
    if (value[i] !== "&") { result += value[i++]; continue; }
    const end = value.indexOf(";", i + 1);
    if (end < 0 || end - i > 14) return null;
    const entity = value.slice(i + 1, end);
    // Avoid prototype-property lookups for untrusted entity names.
    let decoded: string | undefined =
      entity === "amp" ? "&" : entity === "lt" ? "<" :
      entity === "gt" ? ">" : entity === "quot" ? '"' :
      entity === "apos" ? "'" : undefined;
    if (decoded === undefined && /^#(?:x[0-9a-f]+|\d+)$/i.test(entity)) {
      const point = entity[1]?.toLowerCase() === "x"
        ? parseInt(entity.slice(2), 16) : parseInt(entity.slice(1), 10);
      if (point === 0 || point > 0x10ffff || point >= 0xd800 && point <= 0xdfff)
        return null;
      decoded = String.fromCodePoint(point);
    }
    if (decoded === undefined) return null;
    result += decoded;
    i = end + 1;
  }
  return result;
}

function attributes(raw: string, tag: string): Record<string, string> | null {
  const result: Record<string, string> = Object.create(null);
  const attribute = /\s+([A-Za-z][A-Za-z0-9_-]*)\s*=\s*(?:"([^"]*)"|'([^']*)')/y;
  let pos = 0;
  let count = 0;
  while (pos < raw.length) {
    if (/^\s*$/.test(raw.slice(pos))) break;
    attribute.lastIndex = pos;
    const match = attribute.exec(raw);
    if (!match || ++count > 16 || Object.prototype.hasOwnProperty.call(result, match[1]))
      return null;
    const value = decode(match[2] === undefined ? match[3] : match[2]);
    if (value === null || !validAttribute(tag, match[1], value)) return null;
    result[match[1]] = value;
    pos = attribute.lastIndex;
  }
  return result;
}

function allowedChild(parent: string, child: string): boolean {
  return parent === "svg" || parent === "g" ? graphics.has(child) :
    (parent === "text" || parent === "tspan") && child === "tspan";
}

function parseSvg(body: string, rootAttrs: Record<string, string>): SvgNode | null {
  if (body.length > 24000 || Object.keys(rootAttrs).length > 16) return null;
  const rootProps: Record<string, string> = Object.create(null);
  for (const [name, value] of Object.entries(rootAttrs)) {
    if (typeof value !== "string") return null;
    const decoded = decode(value);
    if (decoded === null || !validAttribute("svg", name, decoded)) return null;
    rootProps[name] = decoded;
  }
  const root: SvgNode = { tag: "svg", props: rootProps, children: [] };
  const stack: SvgNode[] = [root];
  let count = 1;
  for (let i = 0; i < body.length;) {
    if (body[i] !== "<") {
      const next = body.indexOf("<", i);
      const end = next === -1 ? body.length : next;
      const text = decode(body.slice(i, end));
      if (text === null) return null;
      const parent = stack[stack.length - 1];
      if (parent.tag === "text" || parent.tag === "tspan") parent.children.push(text);
      else if (text.trim()) return null;
      i = end;
      continue;
    }
    // No declarations, comments, DTD, CDATA, processing instructions or
    // namespaces. These are outside the finite supported SVG dialect.
    const start = i++;
    let quote = "";
    while (i < body.length) {
      const c = body[i++];
      if (quote) { if (c === quote) quote = ""; }
      else if (c === "'" || c === '"') quote = c;
      else if (c === ">") break;
    }
    if (quote || body[i - 1] !== ">" || i - start > 4096) return null;
    const token = body.slice(start + 1, i - 1);
    if (token.startsWith("/")) {
      const match = /^\/([A-Za-z][A-Za-z0-9-]*)\s*$/.exec(token);
      if (!match || stack.length === 1 || stack[stack.length - 1].tag !== match[1])
        return null;
      stack.pop();
      continue;
    }
    const match = /^([A-Za-z][A-Za-z0-9-]*)/.exec(token);
    if (!match || !elements.has(match[1]) ||
        !allowedChild(stack[stack.length - 1].tag, match[1]) || ++count > 250)
      return null;
    const selfClosing = /\/\s*$/.test(token);
    if (leaf.has(match[1]) && !selfClosing) return null;
    const rawAttrs = token.slice(match[0].length, selfClosing ? token.lastIndexOf("/") : undefined);
    const props = attributes(rawAttrs, match[1]);
    if (!props) return null;
    const node: SvgNode = { tag: match[1], props, children: [] };
    stack[stack.length - 1].children.push(node);
    if (!selfClosing) {
      if (stack.length >= 12) return null;
      stack.push(node);
    }
  }
  if (stack.length !== 1) return null;
  root.props.role = "img";
  root.props["aria-label"] = root.props["aria-label"] || "Model diagram";
  root.props.className = "rich-svg";
  root.props.width = "100%";
  return root;
}

function render(node: SvgNode): ReactNode {
  return createElement(node.tag, node.props,
    ...node.children.map(child => typeof child === "string" ? child : render(child)));
}

export function renderSvgBlock(raw: string, rootAttributes: Record<string, string>): ReactNode {
  const parsed = parseSvg(raw, rootAttributes);
  return parsed ? render(parsed) :
    createElement("span", { className: "rich-unknown", role: "note" },
      "Unsupported control: svg — invalid or unsafe SVG markup");
}
