import { createElement, Fragment, useContext, useId, useState } from "react";
import { RichActionContext } from "./RichActionContext";
import type { ReactNode, ComponentType } from "react";
import { RichCodeBlock } from "./RichCodeBlock";
import { RichWritingBlock } from "./RichWritingBlock";
import { Cite } from "./rich-elements/Cite";
import { rowComponent } from "./rich-elements/Row";
import { colComponent } from "./rich-elements/Col";
import { boxComponent } from "./rich-elements/Box";
import { gridComponent } from "./rich-elements/Grid";
import { gridItemComponent } from "./rich-elements/GridItem";
import { textComponent } from "./rich-elements/Text";
import { titleComponent } from "./rich-elements/Title";
import { captionComponent } from "./rich-elements/Caption";
import { dividerComponent } from "./rich-elements/Divider";
import { badgeComponent } from "./rich-elements/Badge";
import { iconComponent } from "./rich-elements/Icon";
import { linkComponent } from "./rich-elements/Link";
import { chartComponent } from "./rich-elements/Chart";
import { svgComponents } from "./RichSvg";
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

const svg = svgComponents();
// Only symbolic server-validated tag names map to host-owned presentation.
const presentation = {
  row: rowComponent,
  col: colComponent,
  box: boxComponent,
  grid: gridComponent,
"grid-item": gridItemComponent,
  text: textComponent,
  title: titleComponent,
  caption: captionComponent,
  divider: dividerComponent,
  badge: badgeComponent,
  icon: iconComponent,
  Link: linkComponent,
  Chart: chartComponent,
};

const safeLink = (url?: string) => {
  if (!url || !/^(https?:\/\/|mailto:)/i.test(url)) return undefined;
  try {
    const resolved = new URL(url);
    return ["http:", "https:", "mailto:"].includes(resolved.protocol) ? url : undefined;
  } catch { return undefined; }
};

function renderNode(node: RichNode, depth: number, svgScope: boolean): ReactNode {
  if (depth > 40) return null;
  const key = node.id;
  const attrs = node.attributes || {};
  const content = () => node.children?.map(child =>
    <Fragment key={child.id}>{renderNode(child, depth + 1, svgScope)}</Fragment>);
  if (node.kind === "literal" || node.kind === "text") return node.text || "";
  if (node.kind === "question") return <CanonicalQuestion key={key} node={node} depth={depth} />;
  if (node.kind === "raw") {
    if (node.name === "CodeBlock")
      return <RichCodeBlock key={key} source={node.text || ""}
        language={/^[\w+#.-]{1,32}$/.test(attrs.language || "") ? attrs.language : "text"}
        editable={attrs.editable === "true"} />;
    if (node.name === "WritingBlock")
      return <RichWritingBlock key={key} source={node.text || ""}
        variant={attrs.variant || "standard"} subject={attrs.subject || ""}
        recipient={attrs.recipient || ""} cc={attrs.cc || ""} bcc={attrs.bcc || ""} />;
    return node.text || "";
  }
  if (node.kind === "element") {
    const name = node.name || "";
    if (name === "Cite") return <Cite key={key} />;
    if (name === "svg" || svgScope) {
      const component = svg["mezhs-svg-" + name];
      if (!component) return null;
      const props: Record<string, unknown> = {};
      for (const [attr, value] of Object.entries(attrs))
        props["data-mezhs-svg-" + attr.toLowerCase()] = value;
      return createElement(component as ComponentType<Record<string, unknown>>, { ...props, key }, node.children?.map(child =>
        <Fragment key={child.id}>{renderNode(child, depth + 1, true)}</Fragment>));
    }
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
        {headers.length > 0 && <thead>{headers.map(row => <Fragment key={row.id}>{renderNode(row, depth + 1, false)}</Fragment>)}</thead>}
        <tbody>{body.map(row => <Fragment key={row.id}>{renderNode(row, depth + 1, false)}</Fragment>)}</tbody>
      </table>;
    }
    case "tableRow": return <tr key={key}>{children}</tr>;
    case "tableCell": return attrs.header === "true"
      ? <th key={key} scope="col">{children}</th> : <td key={key}>{children}</td>;
    default: return node.text || children;
  }
}

function CanonicalQuestion({ node, depth }: { node: RichNode; depth: number }) {
  const onIssueNewTurn = useContext(RichActionContext);
  const groupName = useId();
  const options = (node.children || []).filter(child => child.kind === "questionOption");
  const button = node.children?.find(child => child.kind === "questionButton");
  const [choice, setChoice] = useState(node.attributes?.initial || "");
  const selected = options.find(option => option.attributes?.value === choice);
  const message = selected?.attributes?.message;
  const enabled = !!onIssueNewTurn && typeof message === "string" && message.length > 0;
  if (!button || options.length < 2 || options.length > 12 ||
      !options.some(option => option.attributes?.value === node.attributes?.initial)) return null;
  const label = (children?: RichNode[]) => children?.map(child =>
    <Fragment key={child.id}>{renderNode(child, depth + 1, false)}</Fragment>);
  const control = <fieldset className="rich-question">
    <legend className="rich-question-caption">Choose one option</legend>
    {options.map(option => <label key={option.id} className="rich-question-option">
      <input type="radio" name={groupName} value={option.attributes?.value || ""}
        checked={choice === option.attributes?.value}
        onChange={() => setChoice(option.attributes?.value || "")} />
      <div>{label(option.children || [])}</div>
    </label>)}
    <button type="button" className={button.attributes?.block === "true" ? "rich-question-block" : undefined}
      disabled={!enabled} onClick={() => { if (enabled && message) onIssueNewTurn?.(message); }}>
      {label(button.children || [])}
    </button>
    {!onIssueNewTurn && <small className="rich-question-readonly">Submission unavailable in this view.</small>}
  </fieldset>;
  if (node.attributes?.box !== "true") return control;
  const props: Record<string, string> = {};
  for (const [name, value] of Object.entries(node.attributes || {}))
    if (name.startsWith("box."))
      props["data-mezhs-" + name.slice(4)] = value;
  return createElement(boxComponent, props, control);
}

export function RichDocumentView({ document, source }: { document: RichDocument; source: string }) {
  if (document.version !== 1 || !document.valid)
    return <div className="prompt-text">{source}</div>;
  return <div className="markdown-content">
    {document.nodes.map(node => <Fragment key={node.id}>{renderNode(node, 0, false)}</Fragment>)}
  </div>;
}
