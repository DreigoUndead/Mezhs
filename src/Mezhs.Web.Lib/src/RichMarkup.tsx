import { ReactNode, CSSProperties } from "react";
import JSON5 from "json5";
import type { Components } from "react-markdown";
import { Check, CircleCheck, CircleAlert, CircleX, Info, ArrowDown, GitBranch, GitPullRequest } from "lucide-react";

type MarkupNode = {
  type: string;
  name?: string | null;
  attributes?: Array<{ type: string; name?: string; value?: string | { value: string } | null }>;
  children?: MarkupNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
  data?: { hName?: string; hProperties?: Record<string, string> };
  value?: string;
};

const components = new Set(["row", "col", "box", "grid", "grid-item", "text", "badge", "icon", "Link"]);
const attributes = new Set(["gap", "align", "justify", "color", "name", "size", "border", "strong", "url", "title", "columns", "padding", "radius", "background", "width", "weight", "textAlign"]);

// Parse supported JSX-like markup as DATA. Unknown constructs and executable
// expressions remain escaped text. MDX is never compiled or evaluated.
export function remarkRichMarkup() {
  return (tree: MarkupNode, file: { value: unknown }) => {
    const source = String(file.value);
    const original = (node: MarkupNode) => {
      const start = node.position?.start.offset ?? 0;
      const end = node.position?.end.offset ?? start;
      return { type: "text", value: source.slice(start, end) };
    };
    function visit(parent: MarkupNode) {
      if (!parent.children) return;
      parent.children = parent.children.map(node => {
        if (node.type === "mdxJsxFlowElement" || node.type === "mdxJsxTextElement") {
          if (!node.name || !components.has(node.name)) return original(node);
          const props: Record<string, string> = {};
          for (const attr of node.attributes ?? []) {
            if (attr.type !== "mdxJsxAttribute") return original(node);
            if (!attr.name || !attributes.has(attr.name)) continue;
            const expression = typeof attr.value === "object" && attr.value ? attr.value.value.trim() : "";
            if (expression && !/^(?:\d+(?:\.\d+)?|true|false)$/.test(expression)) return original(node);
            const value = typeof attr.value === "string" ? attr.value
              : expression || (attr.value === null ? "true" : "");
            props["data-mezhs-" + attr.name] = value;
          }
          node.data = { hName: "mezhs-" + node.name.toLowerCase(), hProperties: props };
        } else if (node.type.startsWith("mdx")) {
          return original(node);
        }
        visit(node);
        return node;
      });
    }
    visit(tree);
  };
}

type RichProps = { children?: ReactNode; [key: string]: unknown };
const read = (props: RichProps, key: string) =>
  typeof props["data-mezhs-" + key] === "string" ? props["data-mezhs-" + key] as string : "";

function layout(props: RichProps): CSSProperties {
  const gap = read(props, "gap");
  const align = read(props, "align");
  const justify = read(props, "justify");
  const positions: Record<string, CSSProperties["alignItems"]> = { start: "flex-start", center: "center", end: "flex-end" };
  const justifications: Record<string, CSSProperties["justifyContent"]> = { start: "flex-start", center: "center", end: "flex-end", between: "space-between", around: "space-around" };
  return {
    gap: /^\d+(?:\.\d+)?$/.test(gap) ? Math.min(12, Number(gap)) * 4 : undefined,
    alignItems: positions[align],
    justifyContent: justifications[justify],
  };
}

function surface(props: RichProps): CSSProperties {
  const padding = read(props, "padding");
  const radius = read(props, "radius");
  const width = read(props, "width");
  return {
    ...layout(props),
    padding: /^\d+$/.test(padding) ? Math.min(12, Number(padding)) * 4 : undefined,
    borderRadius: ({ sm: 5, md: 9, lg: 14, full: 999 } as Record<string, number>)[radius],
    background: read(props, "background") === "surface-secondary" ? "#eeebe5" : undefined,
    width: /^(?:100|[1-9]?\d)%$/.test(width) ? width : undefined,
  };
}

function tone(props: RichProps) {
  const color = read(props, "color");
  return /^(success|danger|warning|secondary|muted)$/.test(color) ? " rich-tone-" + color : "";
}

const icons = {
  check: Check, "check-circle": CircleCheck, "circle-check": CircleCheck,
  "alert-circle": CircleAlert, "x-circle": CircleX, info: Info,
  "arrow-down": ArrowDown, "git-branch": GitBranch, "git-pull-request": GitPullRequest,
};

export const richComponents = {
  "mezhs-row": ({ children, ...props }: RichProps) => <div className="rich-row" style={layout(props)}>{children}</div>,
  "mezhs-col": ({ children, ...props }: RichProps) => <div className="rich-col" style={layout(props)}>{children}</div>,
  "mezhs-box": ({ children, ...props }: RichProps) => <div className={"rich-box" + (read(props, "border") === "true" ? " rich-bordered" : "")} style={surface(props)}>{children}</div>,
  "mezhs-grid": ({ children, ...props }: RichProps) => {
    const columns = read(props, "columns");
    return <div className="rich-grid" style={{
      ...layout(props),
      gridTemplateColumns: `repeat(${/^\d+$/.test(columns) ? Math.min(6, Math.max(1, Number(columns))) : 1},minmax(0,1fr))`,
    }}>{children}</div>;
  },
  "mezhs-grid-item": ({ children }: RichProps) => <div className="rich-grid-item">{children}</div>,
  "mezhs-text": ({ children, ...props }: RichProps) => {
    const weight = read(props, "weight");
    const size = read(props, "size");
    return <span className={tone(props).trim()} style={{
      fontWeight: read(props, "strong") === "true" || weight === "bold" ? 700 : weight === "medium" ? 500 : undefined,
      fontSize: ({ "2xs": ".72em", xs: ".8em", sm: ".9em", lg: "1.15em" } as Record<string, string>)[size],
      textAlign: read(props, "textAlign") === "center" ? "center" : undefined,
    }}>{children}</span>;
  },
  "mezhs-badge": ({ children, ...props }: RichProps) => <span className={"rich-badge" + tone(props)}>{children}</span>,
  "mezhs-icon": (props: RichProps) => {
    const name = read(props, "name");
    const Icon = icons[name as keyof typeof icons];
    const size = read(props, "size") === "lg" ? 20 : read(props, "size") === "sm" ? 14 : 16;
    return <span className={"rich-icon" + tone(props)} role="img" aria-label={name || "icon"}>
      {Icon ? <Icon size={size} aria-hidden="true" /> : name}
    </span>;
  },
  "mezhs-link": ({ children, ...props }: RichProps) => {
    const url = read(props, "url");
    const label = children || read(props, "title") || url;
    return /^(https?:\/\/|mailto:)/i.test(url)
      ? <a href={url} rel="noopener noreferrer" target="_blank">{label}</a>
      : <span>{label}</span>;
  },
} as Components;


const maxLoopItems = 40;
const maxTotalIterations = 256;
const maxExpandedLength = 180_000;

// The only supported template data is a JSON5 literal array; functions, calls
// and arbitrary JavaScript are never parsed or executed.
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
      const after = /^\s+as\s+([A-Za-z_$][\w$]*)\s*\}/.exec(source.slice(i + 1));
      if (!after) return null;
      return { literal: source.slice(start, i + 1), name: after[1], end: i + 1 + after[0].length };
    }
  }
  return null;
}

function closingEach(source: string, from: number) {
  let depth = 1;
  let position = from;
  while (position < source.length) {
    const next = source.indexOf("{#each", position);
    const close = source.indexOf("{/each}", position);
    if (close < 0) return null;
    if (next >= 0 && next < close) {
      depth++;
      position = next + 6;
    } else if (--depth === 0) {
      return { start: close, end: close + "{/each}".length };
    } else {
      position = close + "{/each}".length;
    }
  }
  return null;
}

function interpolate(source: string, scope: Record<string, unknown>) {
  return source.replace(/\{([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\}/g, (match, path: string) => {
    const segments = path.split(".");
    let value: unknown = scope;
    for (const segment of segments) {
      if (value === null || typeof value !== "object" ||
          !Object.prototype.hasOwnProperty.call(value, segment)) return match;
      value = (value as Record<string, unknown>)[segment];
    }
    if (typeof value !== "string" && typeof value !== "number" && typeof value !== "boolean") return match;
    return String(value).replace(/&/g, "&amp;").replace(/</g, "&lt;")
      .replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  });
}

function expandEach(source: string, scope: Record<string, unknown>, level: number, budget: { iterations: number }): string {
  if (level > 4) throw new Error("Nested UI templates exceed limit");
  let result = "";
  let cursor = 0;
  while (true) {
    const start = source.indexOf("{#each", cursor);
    if (start < 0) break;
    const opening = parseEach(source, start);
    const closing = opening && closingEach(source, opening.end);
    if (!opening || !closing) throw new Error("Invalid UI template");
    const values: unknown = JSON5.parse(opening.literal);
    if (!Array.isArray(values) || values.length > maxLoopItems) throw new Error("Invalid UI template data");
    result += interpolate(source.slice(cursor, start), scope);
    const body = source.slice(opening.end, closing.start);
    for (const value of values) {
      if (++budget.iterations > maxTotalIterations) throw new Error("Too many UI template iterations");
      result += expandEach(body, { ...scope, [opening.name]: value }, level + 1, budget);
      if (result.length > maxExpandedLength) throw new Error("Expanded UI too large");
    }
    cursor = closing.end;
  }
  result += interpolate(source.slice(cursor), scope);
  if (result.length > maxExpandedLength) throw new Error("Expanded UI too large");
  return result;
}

// Leave fenced source examples intact: a fenced DIL snippet is documentation,
// not an instruction to render a new UI.
function shieldFences(source: string) {
  const fences: string[] = [];
  const lines = source.match(/[^\n]*\n|[^\n]+$/g) ?? [];
  let output = "";
  let open = "";
  let block = "";
  for (const line of lines) {
    const marker = /^ {0,3}(`{3,}|~{3,})/.exec(line)?.[1];
    if (!open) {
      if (!marker) { output += line; continue; }
      open = marker;
      block = line;
    } else {
      block += line;
      if (marker && marker[0] === open[0] && marker.length >= open.length &&
          /^\s*$/.test(line.slice(line.indexOf(marker) + marker.length))) {
        output += "\uE000" + fences.length + "\uE001";
        fences.push(block);
        block = "";
        open = "";
      }
    }
  }
  if (open) {
    output += "\uE000" + fences.length + "\uE001";
    fences.push(block);
  }
  return { source: output, fences };
}

export function expandRichTemplates(content: string) {
  if (!content.includes("{#each")) return content;
  const { source, fences } = shieldFences(content);
  try {
    return expandEach(source, {}, 0, { iterations: 0 }).replace(/\uE000(\d+)\uE001/g, (_, n: string) => fences[Number(n)] ?? "");
  } catch {
    return content; // Unknown or invalid syntax remains readable.
  }
}
