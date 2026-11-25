import { createElement, Fragment } from "react";
import type { ReactNode, ComponentType } from "react";
import { presentation, rawBlocks } from "./rich-elements/Registry";
import { CanonicalQuestion } from "./rich-elements/Question";
import "./markdown.css";

// The server is the only owner of document grammar. These are data, not
// executable JSX, callbacks, HTML or identifiers to dynamically import.
export type RichNode = {
  kind: string;
  id: string;
  sourceSpan: { start: number; end: number };
  text?: string | null;
  name?: string | null;
  attributes?: Record<string, string> | null;
  children?: RichNode[] | null;
};

export type RichDocument = {
  version: number;
  valid: boolean;
  nodes: RichNode[];
  diagnostics: Array<{ code: string; sourceSpan: { start: number; end: number } }>;
};

const safeLink = (url?: string) => {
  if (!url || !/^(https?:\/\/|mailto:)/i.test(url)) return undefined;
  try {
    const resolved = new URL(url);
    return ["http:", "https:", "mailto:"].includes(resolved.protocol) ? url : undefined;
  } catch { return undefined; }
};

function renderNode(node: RichNode, depth: number): ReactNode {
  if (depth > 40) return null;
  const key = node.id;
  const attrs = node.attributes || {};
  const content = () => node.children?.map(child =>
    <Fragment key={child.id}>{renderNode(child, depth + 1)}</Fragment>);
  if (node.kind === "literal" && node.name === "escape") return <span key={key} className="rich-escape">{node.text || ""}</span>;
  if (node.kind === "literal" || node.kind === "text") return node.text || "";
  if (node.kind === "unknown") return <span key={key} className="rich-unknown" role="note">
    <span className="rich-unknown-heading">Unsupported control: {node.name || "unknown"}</span>
    <span className="rich-unknown-reason">{attrs.reason || "Unsupported markup"}</span>
    <span className="rich-unknown-content">{content()}</span>
  </span>;
  if (node.kind === "question") return <CanonicalQuestion key={key} node={node}
    renderChild={child => renderNode(child, depth + 1)} />;
  if (node.kind === "raw") {
    const name = node.name || "";
    const factory = Object.prototype.hasOwnProperty.call(rawBlocks, name)
      ? rawBlocks[name as keyof typeof rawBlocks] : null;
    return factory ? <Fragment key={key}>{factory(node.text || "", attrs)}</Fragment> : node.text || "";
  }
  if (node.kind === "element") {
    const name = node.name || "";
    const element = Object.prototype.hasOwnProperty.call(presentation, name)
      ? presentation[name as keyof typeof presentation] : null;
    if (!element) return null;
    const props: Record<string, unknown> = {};
    for (const [attr, value] of Object.entries(attrs))
      props["data-mezhs-" + attr] = value;
    return createElement(element as ComponentType<Record<string, unknown>>, { ...props, key }, content());
  }
  const children = content();
  switch (node.kind) {
    case "paragraph": return <p key={key}>{children}</p>;
    case "heading": {
      const level = Number(attrs.level || "2");
      const tag = (["h1", "h2", "h3", "h4", "h5", "h6"] as const)[Math.max(0, Math.min(5, level - 1))];
      return createElement(tag, { key }, children);
    }
    case "list": return attrs.ordered === "true" ? <ol key={key}>{children}</ol> : <ul key={key}>{children}</ul>;
    case "listItem": return <li key={key}>{children}</li>;
    case "blockquote": return <blockquote key={key}>{children}</blockquote>;
    case "code": return <pre key={key}><code>{node.text || ""}</code></pre>;
    case "taskCheckbox": return <input key={key} type="checkbox" checked={attrs.checked === "true"}
      disabled aria-label={attrs.checked === "true" ? "Completed task" : "Incomplete task"} />;
    case "inlineCode": return <code key={key}>{node.text || ""}</code>;
    case "strong": return <strong key={key}>{children}</strong>;
    case "emphasis": return <em key={key}>{children}</em>;
    case "strikethrough": return <del key={key}>{children}</del>;
    case "lineBreak": return <br key={key} />;
    case "divider": return <hr key={key} />;
    case "link": {
      const href = safeLink(attrs.url);
      return href ? <a key={key} href={href} rel="noopener noreferrer" target="_blank">{children}</a> : <span key={key}>{children}</span>;
    }
    case "image": {
      const src = safeLink(attrs.url);
      if (!src || !/^https?:/i.test(src)) return children;
      const altText = (nodes: RichNode[] = []): string => nodes.map(child =>
        child.kind === "text" || child.kind === "literal" ? child.text || "" : altText(child.children || [])).join("");
      return <img key={key} src={src} alt={altText(node.children || []).slice(0, 256)}
        loading="lazy" referrerPolicy="no-referrer" />;
    }
    case "table": {
      const rows = node.children || [];
      const headers = rows.filter(row => row.attributes?.header === "true");
      const body = rows.filter(row => row.attributes?.header !== "true");
      return <table key={key}>
        {headers.length > 0 && <thead>{headers.map(row => <Fragment key={row.id}>{renderNode(row, depth + 1)}</Fragment>)}</thead>}
        <tbody>{body.map(row => <Fragment key={row.id}>{renderNode(row, depth + 1)}</Fragment>)}</tbody>
      </table>;
    }
    case "tableRow": return <tr key={key}>{children}</tr>;
    case "tableCell": return attrs.header === "true"
      ? <th key={key} scope="col">{children}</th> : <td key={key}>{children}</td>;
    default: return node.text || children;
  }
}

export function RichDocumentView({ document, source }: { document: RichDocument; source: string }) {
  if (document.version !== 1 || !document.valid)
    return <div className="prompt-text">{source}</div>;
  return <div className="markdown-content">
    {document.nodes.map(node => <Fragment key={node.id}>{renderNode(node, 0)}</Fragment>)}
  </div>;
}
