import { ReactNode, CSSProperties } from "react";
import type { Components } from "react-markdown";
import { Check, CircleCheck, CircleAlert, CircleX, Info, ArrowDown, ArrowRight, GitBranch, GitPullRequest, ScrollText, ChartNoAxesCombined, Network, Database } from "lucide-react";
import { RichChart } from "./RichChart";
import { prepareSvg, svgComponents } from "./RichSvg";

type MarkupNode = {
  type: string;
  name?: string | null;
  attributes?: Array<{ type: string; name?: string; value?: string | { value: string } | null }>;
  children?: MarkupNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
  data?: { hName?: string; hProperties?: Record<string, string> };
  value?: string;
};

const components = new Set(["row", "col", "box", "grid", "grid-item", "text", "title", "divider", "badge", "icon", "caption", "Link", "Chart"]);
const attributes = new Set(["gap", "align", "justify", "color", "name", "size", "border", "strong", "url", "title", "columns", "padding", "radius", "background", "width", "weight", "textAlign", "key", "flex", "tabularNums", "content", "inline"]);

// Parse supported JSX-like markup as DATA. Unknown constructs and executable
// expressions remain escaped text. MDX is never compiled or evaluated.
export function remarkRichMarkup(slotOffsets: Record<string, number> = Object.create(null)) {
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
          if (node.name === "svg") {
            // The entire subtree passes one atomic validator before React gets any nodes.
            return prepareSvg(node) ? node : original(node);
          }
          if (node.name === "Cite") {
            // Source IDs are transient per ChatGPT reply. MEŽS does not
            // receive a trusted citation-ID to URL map; never fabricate links.
            const attrs = node.attributes || [];
            const safeId = (value: unknown): value is string =>
              typeof value === "string" &&
              /^turn[0-9]{1,12}(?:search|news|web)[0-9]{1,6}$/.test(value);
            let ids: string[] = [];
            if (attrs.length === 1 && attrs[0].type === "mdxJsxAttribute" && attrs[0].name === "ref" &&
                typeof attrs[0].value === "string" && safeId(attrs[0].value)) {
              ids = [attrs[0].value];
            } else if (attrs.length === 1 && attrs[0].type === "mdxJsxAttribute" &&
                       attrs[0].name === "refs") {
              // Accept JSON literal arrays both directly from the MDX
              // expression AST and from the restricted template expander's
              // encoded json: form. Neither path evaluates model expressions.
              const raw = attrs[0].value;
              const value = typeof raw === "string" ? raw :
                raw && typeof raw === "object" ? raw.value : "";
              if (value.length < 800) {
                try {
                  const decoded: unknown = JSON.parse(value.startsWith("json:")
                    ? decodeURIComponent(value.slice(5)) : value);
                  if (Array.isArray(decoded) && decoded.length > 0 && decoded.length <= 8 &&
                      decoded.every(safeId)) ids = decoded;
                } catch { /* Not a supported literal citation list. */ }
              }
            }
            if (!ids.length || (node.children || []).length) return original(node);
            node.data = { hName: "mezhs-cite", hProperties: {} };
            return node;
          }
          if (node.name === "mezhs-question-slot") {
            const attrs = node.attributes || [];
            const id = attrs.length === 1 && attrs[0].type === "mdxJsxAttribute" &&
              attrs[0].name === "id" && typeof attrs[0].value === "string" ? attrs[0].value : "";
            if (!Object.prototype.hasOwnProperty.call(slotOffsets, id) ||
                node.position?.start.offset !== slotOffsets[id] ||
                (node.children || []).length) return original(node);
            node.data = { hName: "mezhs-question", hProperties: { "data-mezhs-question-id": id } };
            return node;
          }
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

function boundedSpacing(value: unknown): number | undefined {
  const number = typeof value === "number" ? value : typeof value === "string" && /^\d+$/.test(value) ? Number(value) : NaN;
  return Number.isFinite(number) && number >= 0 && number <= 12 ? number * 4 : undefined;
}

function structured(props: RichProps, key: string): Record<string, unknown> | null {
  const value = read(props, key);
  if (!value.startsWith("json:")) return null;
  try {
    const data: unknown = JSON.parse(decodeURIComponent(value.slice(5)));
    return data && typeof data === "object" && !Array.isArray(data) ? data as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

function surface(props: RichProps): CSSProperties {
  const padding = read(props, "padding");
  const paddingObject = structured(props, "padding");
  const border = structured(props, "border");
  const left = border?.left && typeof border.left === "object" ? border.left as Record<string, unknown> : null;
  const width = read(props, "width");
  const flex = read(props, "flex");
  const radius = read(props, "radius");
  const leftSize = boundedSpacing(left?.size);
  return {
    ...layout(props),
    padding: boundedSpacing(padding),
    paddingLeft: boundedSpacing(paddingObject?.left),
    borderRadius: ({ sm: 5, md: 9, lg: 14, xl: 18, full: 999 } as Record<string, number>)[radius],
    background: read(props, "background") === "surface-secondary" ? "#eeebe5" : undefined,
    width: /^(?:100|[1-9]?\d)%$/.test(width) ||
      /^\d{1,3}(?:\.\d+)?px$/.test(width) && parseFloat(width) <= 500 ? width : undefined,
    flex: /^(?:1|2|3)$/.test(flex) ? Number(flex) : undefined,
    borderLeft: leftSize !== undefined && leftSize > 0
      ? (leftSize / 4) + "px solid " + (left?.color === "strong" ? "#504e49" : "#d5d0c6")
      : undefined,
  };
}

function tone(props: RichProps) {
  const color = read(props, "color");
  return /^(success|danger|warning|secondary|muted)$/.test(color) ? " rich-tone-" + color : "";
}

const icons = {
  check: Check, "check-circle": CircleCheck, "circle-check": CircleCheck,
  "alert-circle": CircleAlert, "x-circle": CircleX, info: Info,
  "arrow-down": ArrowDown, "arrow-right": ArrowRight, "git-branch": GitBranch, "git-pull-request": GitPullRequest,
  "scroll-text": ScrollText, "chart-no-axes-combined": ChartNoAxesCombined,
  network: Network, database: Database,
};

export const richComponents = {
  ...svgComponents(),
  "mezhs-chart": (props: RichProps) => <RichChart content={structured(props, "content")} />,
  "mezhs-cite": (props: RichProps) => <span className="rich-citation-unavailable" role="note" title="Source reference unavailable in this MEŽS transcript">[source unavailable]</span>,
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
      fontVariantNumeric: read(props, "tabularNums") === "true" ? "tabular-nums" : undefined,
    }}>{children}</span>;
  },

  "mezhs-title": ({ children, ...props }: RichProps) => {
    const size = read(props, "size");
    return <span className={"rich-title" + tone(props)} style={{
      fontSize: ({ xs: ".9em", sm: "1em", md: "1.12em", lg: "1.3em", xl: "1.55em" } as Record<string, string>)[size],
      fontVariantNumeric: read(props, "tabularNums") === "true" ? "tabular-nums" : undefined,
      textAlign: read(props, "textAlign") === "center" ? "center" : undefined,
    }}>{children}</span>;
  },
  "mezhs-caption": ({ children }: RichProps) => <span className="rich-caption">{children}</span>,
  "mezhs-divider": (props: RichProps) => <hr className={"rich-divider" + (read(props, "color") === "strong" ? " rich-divider-strong" : "")} />,
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
