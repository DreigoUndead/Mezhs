import type { Components } from "react-markdown";
import { prepareSvg, svgComponents } from "./RichSvg";
import { RichWritingBlockSlot } from "./RichWritingBlock";
import { RichCodeBlockSlot } from "./RichCodeBlock";
import { prepareCite, Cite } from "./rich-elements/Cite";
import { richElementRegistry, richStaticComponents } from "./rich-elements";
import { prepareStaticElement, type MarkupNode } from "./rich-elements/Element";

// Fixed host-owned dispatch. No MDX compilation, model JS or dynamic imports.
export function remarkRichMarkup(slotOffsets: Record<string, number> = Object.create(null),
  writingOffsets: Record<string, number> = Object.create(null),
  codeOffsets: Record<string, number> = Object.create(null)) {
  return (tree: MarkupNode, file: { value: unknown }) => {
    const hostSlots: Record<string, { offsets: Record<string, number>; tag: string; idProp: string }> =
      Object.assign(Object.create(null), {
        "mezhs-question-slot": { offsets: slotOffsets, tag: "mezhs-question", idProp: "data-mezhs-question-id" },
        "mezhs-writing-slot": { offsets: writingOffsets, tag: "mezhs-writing-block", idProp: "data-mezhs-writing-id" },
        "mezhs-code-slot": { offsets: codeOffsets, tag: "mezhs-code-block", idProp: "data-mezhs-code-id" },
      });
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
          if (node.name === "svg")
            return prepareSvg(node) ? node : original(node);
          if (node.name === "Cite")
            return prepareCite(node) ? node : original(node);
          const slot = node.name && hostSlots[node.name];
          if (slot) {
            const attrs = node.attributes || [];
            const id = attrs.length === 1 && attrs[0].type === "mdxJsxAttribute" &&
              attrs[0].name === "id" && typeof attrs[0].value === "string" ? attrs[0].value : "";
            if (!Object.prototype.hasOwnProperty.call(slot.offsets, id) ||
                node.position?.start.offset !== slot.offsets[id] ||
                (node.children || []).length) return original(node);
            node.data = { hName: slot.tag, hProperties: { [slot.idProp]: id } };
            return node;
          }
          const element = node.name && richElementRegistry[node.name];
          if (!element || !prepareStaticElement(node, element)) return original(node);
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
export const richComponents = {
  ...svgComponents(),
  ...richStaticComponents,
  "mezhs-writing-block": RichWritingBlockSlot,
  "mezhs-code-block": RichCodeBlockSlot,
  "mezhs-cite": Cite,
} as Components;
