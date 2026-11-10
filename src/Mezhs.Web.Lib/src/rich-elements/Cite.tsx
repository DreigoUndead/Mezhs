import type { MarkupNode, RichProps } from "./Element";
export function prepareCite(node: MarkupNode): boolean {
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
            if (!ids.length || (node.children || []).length) return false;
            node.data = { hName: "mezhs-cite", hProperties: {} };
            return true;
}
export function Cite(_props: RichProps) {
  return <span className="rich-citation-unavailable" role="note"
    title="Source reference unavailable in this MEŽS transcript">[source unavailable]</span>;
}
