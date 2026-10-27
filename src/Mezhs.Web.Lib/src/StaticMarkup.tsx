import { CSSProperties, Fragment, ReactNode } from "react";

// Models can emit static ChatGPT-style presentation tags. This is a deliberately
// small, inert vocabulary, NOT JSX/HTML evaluation or a full DIL runtime.
export type StaticPart = string | StaticElement;
export type StaticElement = {
  tag: string;
  props: Record<string, string>;
  children: StaticPart[];
};

const supported = new Set([
  "row", "col", "box", "text", "badge", "icon", "Link",
  "title", "caption", "bold", "italic", "underline", "divider",
  "list", "list-item", "table", "table-row", "table-cell", "escape",
]);

const tagPattern = /`[^`\n]+`|<\/?([A-Za-z][A-Za-z0-9-]*)\b[^<>]*>/g;
const attributePattern = /([A-Za-z][\w-]*)\s*=\s*(?:"([^"]*)"|'([^']*)'|\{([^{}]*)\}|([^\s>]+))/g;

function attributes(source: string) {
  const props: Record<string, string> = {};
  for (const item of source.matchAll(attributePattern))
    props[item[1]] = item[2] ?? item[3] ?? item[4] ?? item[5] ?? "";
  return props;
}

// Fenced code must remain literal even if it contains apparent UI tags.
function fencedRanges(source: string): Array<[number, number]> {
  const ranges: Array<[number, number]> = [];
  let start = -1;
  let marker = "";
  let size = 0;
  let offset = 0;
  for (const line of source.match(/[^\n]*(?:\n|$)/g) ?? []) {
    if (!line) continue;
    if (start === -1) {
      const open = line.match(/^ {0,3}(`{3,}|~{3,})/);
      if (open) {
        start = offset;
        marker = open[1][0];
        size = open[1].length;
      }
    } else if (new RegExp("^ {0,3}" + marker + "{" + size + ",}\\s*$").test(line.trimEnd())) {
      ranges.push([start, offset + line.length]);
      start = -1;
    }
    offset += line.length;
  }
  if (start !== -1) ranges.push([start, source.length]);
  return ranges;
}

export function parseStaticMarkup(source: string): StaticPart[] {
  const root: StaticPart[] = [];
  const stack: StaticElement[] = [];
  const ranges = fencedRanges(source);
  let rangeIndex = 0;
  let cursor = 0;

  function append(value: StaticPart) {
    const target = stack.length ? stack[stack.length - 1].children : root;
    if (typeof value === "string" && typeof target[target.length - 1] === "string")
      target[target.length - 1] = (target[target.length - 1] as string) + value;
    else if (value !== "") target.push(value);
  }

  for (const match of source.matchAll(tagPattern)) {
    const at = match.index ?? 0;
    while (rangeIndex < ranges.length && at >= ranges[rangeIndex][1]) rangeIndex++;
    if ((rangeIndex < ranges.length && at >= ranges[rangeIndex][0]) || match[0].startsWith("`"))
      continue;

    const tag = match[1];
    if (!tag || !supported.has(tag) || (stack.length && stack[stack.length - 1].tag === "escape" && match[0] !== "</escape>"))
      continue;

    append(source.slice(cursor, at));
    cursor = at + match[0].length;
    const closing = match[0].startsWith("</");
    if (closing) {
      if (stack.length && stack[stack.length - 1].tag === tag) stack.pop();
      else append(match[0]); // Mismatched tags stay visible as text.
      continue;
    }

    if (stack.length >= 24) { append(match[0]); continue; }
    const selfClosing = /\/\s*>$/.test(match[0]) || tag === "icon" || tag === "divider";
    const element: StaticElement = {
      tag,
      props: attributes(match[0].slice(1 + tag.length, -1)),
      children: [],
    };
    append(element);
    if (!selfClosing) stack.push(element);
  }
  append(source.slice(cursor));
  return root;
}

const palette: Record<string, string> = {
  secondary: "#746f66",
  muted: "#746f66",
  success: "#477829",
  danger: "#b13e35",
  warning: "#9e651b",
  default: "inherit",
};
const icons: Record<string, string> = {
  "check-circle": "✓", "check": "✓", "git-pull-request": "⑂",
  "git-branch": "⑂", "alert-circle": "!", "info": "i",
  "x-circle": "×", "circle-check": "✓",
};

function color(value?: string) {
  return value && Object.prototype.hasOwnProperty.call(palette, value) ? palette[value] : undefined;
}
function spacing(value?: string) {
  if (!value || !/^\d+(?:\.\d+)?$/.test(value)) return undefined;
  return Math.min(Number(value), 12) * 4;
}
function alignment(value?: string): CSSProperties["alignItems"] {
  if (value === "start") return "flex-start";
  if (value === "end") return "flex-end";
  if (value === "center" || value === "stretch" || value === "baseline") return value;
  return undefined;
}
function justification(value?: string): CSSProperties["justifyContent"] {
  if (value === "between") return "space-between";
  if (value === "around") return "space-around";
  if (value === "evenly") return "space-evenly";
  if (value === "start") return "flex-start";
  if (value === "end") return "flex-end";
  if (value === "center") return "center";
  return undefined;
}
function safeHref(value?: string) {
  return value && /^(https?:|mailto:)/i.test(value) ? value : undefined;
}

export function renderStaticMarkup(
  element: StaticElement,
  render: (content: string, inline: boolean) => ReactNode,
  key: number,
): ReactNode {
  const { tag, props } = element;
  function children(inline: boolean): ReactNode[] {
    return element.children.map((item, index) => typeof item === "string"
      ? <Fragment key={index}>{render(item, inline)}</Fragment>
      : <Fragment key={index}>{renderStaticMarkup(item, render, index)}</Fragment>);
  }

  const layout = {
    gap: spacing(props.gap),
    alignItems: alignment(props.align),
    justifyContent: justification(props.justify),
  };
  switch (tag) {
    case "row": return <div key={key} className="rich-row" style={layout}>{children(true)}</div>;
    case "col": return <div key={key} className="rich-col" style={layout}>{children(false)}</div>;
    case "box": return <div key={key} className={"rich-box" + (props.border ? " rich-bordered" : "")} style={layout}>{children(false)}</div>;
    case "text": return <span key={key} style={{ color: color(props.color), fontWeight: props.strong === "true" ? 700 : undefined }}>{children(true)}</span>;
    case "badge": return <span key={key} className={"rich-badge" + (props.color === "success" ? " rich-success" : "")}>{children(true)}</span>;
    case "icon": return <span key={key} className="rich-icon" role="img" aria-label={props.name ?? "icon"}>{icons[props.name] ?? "◆"}</span>;
    case "Link": {
      const href = safeHref(props.url);
      const label = element.children.length ? children(true) : (props.title ?? href ?? "Link");
      return href
        ? <a key={key} href={href} target="_blank" rel="noopener noreferrer">{label}</a>
        : <span key={key}>{label}</span>;
    }
    case "title": return <strong key={key} className="rich-title">{children(true)}</strong>;
    case "caption": return <span key={key} className="rich-caption">{children(true)}</span>;
    case "bold": return <strong key={key}>{children(true)}</strong>;
    case "italic": return <em key={key}>{children(true)}</em>;
    case "underline": return <u key={key}>{children(true)}</u>;
    case "divider": return <hr key={key} />;
    case "list": return <ul key={key}>{children(false)}</ul>;
    case "list-item": return <li key={key}>{children(true)}</li>;
    case "table": return <div key={key} className="markdown-table-wrap"><table><tbody>{children(false)}</tbody></table></div>;
    case "table-row": return <tr key={key}>{children(false)}</tr>;
    case "table-cell": return <td key={key}>{children(true)}</td>;
    case "escape": return <span key={key}>{element.children.map(x => typeof x === "string" ? x : "").join("")}</span>;
    default: return null;
  }
}
