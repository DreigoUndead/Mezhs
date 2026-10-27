import { CSSProperties, ReactNode } from "react";
import type { Components } from "react-markdown";

type UiNode = {
  type: string;
  name?: string | null;
  value?: string;
  children?: UiNode[];
  attributes?: Array<{ type: string; name?: string; value?: string | { value: string } | null }>;
  position?: { start: { offset?: number }; end: { offset?: number } };
  data?: { hName?: string; hProperties?: Record<string, string> };
};

const tags = [
  "row", "col", "box", "text", "badge", "icon", "Link",
  "title", "caption", "bold", "italic", "underline", "divider",
  "list", "list-item", "table", "table-row", "table-cell",
] as const;
const allowed = new Set<string>(tags);
const properties = new Set(["gap", "align", "justify", "color", "name", "url", "title", "border", "strong"]);

// MDX is parsed, never compiled or executed. Only these inert tags and
// literal attributes become React components; everything else stays text.
export function remarkStaticUi() {
  return (tree: UiNode, file: { value: unknown }) => {
    const source = String(file.value);
    function visit(node: UiNode) {
      if (!node.children) return;
      node.children = node.children.map(child => {
        if (child.type.startsWith("mdx")) {
          const start = child.position?.start.offset ?? 0;
          const end = child.position?.end.offset ?? start;
          const original = source.slice(start, end);
          if (child.name === "escape") {
            return { type: "text", value: original.replace(/^<escape[^>]*>/, "").replace(/<\/escape>$/, "") };
          }
          if (!child.name || !allowed.has(child.name)) return { type: "text", value: original };
          const safe: Record<string, string> = {};
          for (const attribute of child.attributes ?? []) {
            if (attribute.type !== "mdxJsxAttribute" || !attribute.name || !properties.has(attribute.name))
              continue;
            const value = typeof attribute.value === "string" ? attribute.value
              : typeof attribute.value?.value === "string" && /^(?:\d+(?:\.\d+)?|true|false)$/.test(attribute.value.value.trim())
                ? attribute.value.value.trim()
                : attribute.value === null ? "true" : undefined;
            if (value !== undefined) safe["data-mezhs-" + attribute.name] = value;
          }
          child.data = { hName: "mezhs-" + child.name.toLowerCase(), hProperties: safe };
        }
        visit(child);
        return child;
      });
    }
    visit(tree);
  };
}

type UiProps = { children?: ReactNode; [key: string]: unknown };
const colors: Record<string, string> = {
  secondary: "#746f66", muted: "#746f66", success: "#477829",
  danger: "#b13e35", warning: "#9e651b",
};
const icons: Record<string, string> = {
  "check-circle": "✓", check: "✓", "circle-check": "✓",
  "git-pull-request": "⑂", "git-branch": "⑂",
  "alert-circle": "!", info: "i", "x-circle": "×",
};

function RichPart({ kind, ...props }: UiProps & { kind: string }) {
  const value = (key: string) => typeof props["data-mezhs-" + key] === "string"
    ? props["data-mezhs-" + key] as string : "";
  const gap = value("gap");
  const align = value("align");
  const justify = value("justify");
  const styles: CSSProperties = {
    gap: /^\d+(?:\.\d+)?$/.test(gap) ? Math.min(Number(gap), 12) * 4 : undefined,
    alignItems: align === "center" ? "center" : align === "start" ? "flex-start" : align === "end" ? "flex-end" : undefined,
    justifyContent: justify === "between" ? "space-between" : justify === "center" ? "center" : undefined,
  };
  const children = props.children;
  switch (kind) {
    case "row": return <div className="rich-row" style={styles}>{children}</div>;
    case "col": return <div className="rich-col" style={styles}>{children}</div>;
    case "box": return <div className={"rich-box" + (value("border") === "true" ? " rich-bordered" : "")} style={styles}>{children}</div>;
    case "text": return <span style={{ color: colors[value("color")], fontWeight: value("strong") === "true" ? 700 : undefined }}>{children}</span>;
    case "badge": return <span className={"rich-badge" + (value("color") === "success" ? " rich-success" : "")}>{children}</span>;
    case "icon": return <span className="rich-icon" role="img" aria-label={value("name") || "icon"}>{icons[value("name")] ?? "◆"}</span>;
    case "Link": {
      const url = value("url");
      const label = children || value("title") || "Link";
      return /^(https?:|mailto:)/i.test(url)
        ? <a href={url} target="_blank" rel="noopener noreferrer">{label}</a> : <span>{label}</span>;
    }
    case "title": return <strong className="rich-title">{children}</strong>;
    case "caption": return <span className="rich-caption">{children}</span>;
    case "bold": return <strong>{children}</strong>;
    case "italic": return <em>{children}</em>;
    case "underline": return <u>{children}</u>;
    case "divider": return <hr />;
    case "list": return <ul>{children}</ul>;
    case "list-item": return <li>{children}</li>;
    case "table": return <div className="markdown-table-wrap"><table><tbody>{children}</tbody></table></div>;
    case "table-row": return <tr>{children}</tr>;
    case "table-cell": return <td>{children}</td>;
    default: return <>{children}</>;
  }
}

export const uiComponents = Object.fromEntries(tags.map(kind => [
  "mezhs-" + kind.toLowerCase(),
  (props: UiProps) => <RichPart kind={kind} {...props} />,
])) as Components;
