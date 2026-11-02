import { ReactNode, CSSProperties } from "react";
import type { Components } from "react-markdown";
import { Check, CircleCheck, CircleAlert, CircleX, Info, GitBranch, GitPullRequest } from "lucide-react";

type MarkupNode = {
  type: string;
  name?: string | null;
  attributes?: Array<{ type: string; name?: string; value?: string | { value: string } | null }>;
  children?: MarkupNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
  data?: { hName?: string; hProperties?: Record<string, string> };
  value?: string;
};

const components = new Set(["row", "col", "box", "text", "badge", "icon", "Link"]);
const attributes = new Set(["gap", "align", "justify", "color", "name", "size", "border", "strong", "url", "title"]);

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
  const positions: Record<string, string> = { start: "flex-start", center: "center", end: "flex-end" };
  const justifications: Record<string, string> = { ...positions, between: "space-between", around: "space-around" };
  return {
    gap: /^\d+(?:\.\d+)?$/.test(gap) ? Math.min(12, Number(gap)) * 4 : undefined,
    alignItems: positions[align],
    justifyContent: justifications[justify],
  };
}

function tone(props: RichProps) {
  const color = read(props, "color");
  return /^(success|danger|warning|secondary|muted)$/.test(color) ? " rich-tone-" + color : "";
}

const icons = {
  check: Check, "check-circle": CircleCheck, "circle-check": CircleCheck,
  "alert-circle": CircleAlert, "x-circle": CircleX, info: Info,
  "git-branch": GitBranch, "git-pull-request": GitPullRequest,
};

export const richComponents = {
  "mezhs-row": ({ children, ...props }: RichProps) => <div className="rich-row" style={layout(props)}>{children}</div>,
  "mezhs-col": ({ children, ...props }: RichProps) => <div className="rich-col" style={layout(props)}>{children}</div>,
  "mezhs-box": ({ children, ...props }: RichProps) => <div className={"rich-box" + (read(props, "border") === "true" ? " rich-bordered" : "")} style={layout(props)}>{children}</div>,
  "mezhs-text": ({ children, ...props }: RichProps) => <span className={tone(props).trim()} style={{ fontWeight: read(props, "strong") === "true" ? 700 : undefined }}>{children}</span>,
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
