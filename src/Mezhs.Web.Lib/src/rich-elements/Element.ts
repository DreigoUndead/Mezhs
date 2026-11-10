import type { ReactNode } from "react";
export type RichProps = { children?: ReactNode; [key: string]: unknown };
export type MarkupNode = {
  type: string; name?: string | null;
  attributes?: Array<{ type: string; name?: string; value?: string | { value: string } | null }>;
  children?: MarkupNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
  data?: { hName?: string; hProperties?: Record<string, string> };
  value?: string;
};
export type RichElement = { tag: string; attributes: ReadonlySet<string>; component: (props: RichProps) => ReactNode };
export function defineElement(tag: string, attributes: string[],
  component: (props: RichProps) => ReactNode): RichElement {
  return { tag, attributes: new Set(attributes), component };
}
// A model may select only pre-registered tags. Each element owns its whitelist.
export function prepareStaticElement(node: MarkupNode, element: RichElement): boolean {
  const props: Record<string, string> = Object.create(null);
  for (const attr of node.attributes || []) {
    if (attr.type !== "mdxJsxAttribute" || !attr.name ||
        !element.attributes.has(attr.name) || Object.prototype.hasOwnProperty.call(props, "data-mezhs-" + attr.name))
      return false;
    const expression = typeof attr.value === "object" && attr.value ? attr.value.value.trim() : "";
    if (expression && !/^(?:\d+(?:\.\d+)?|true|false)$/.test(expression))
      return false;
    const value = typeof attr.value === "string" ? attr.value :
      expression || (attr.value === null ? "true" : "");
    props["data-mezhs-" + attr.name] = value;
  }
  node.data = { hName: "mezhs-" + element.tag.toLowerCase(), hProperties: props };
  return true;
}
