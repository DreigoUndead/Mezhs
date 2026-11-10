import type { Components } from "react-markdown";
import { prepareSvg, svgComponents } from "./RichSvg";
import { prepareWritingBlock, RichWritingBlock } from "./RichWritingBlock";
import { prepareCite, Cite } from "./rich-elements/Cite";
import { richElementRegistry, richStaticComponents } from "./rich-elements";
import { prepareStaticElement, type MarkupNode, type RichProps } from "./rich-elements/Element";
import { read } from "./rich-elements/Style";

// Fixed host-owned dispatch. No MDX compilation, model JS or dynamic imports.
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
          if (node.name === "svg")
            return prepareSvg(node) ? node : original(node);
          if (node.name === "WritingBlock")
            return prepareWritingBlock(node, source) ? node : original(node);
          if (node.name === "Cite")
            return prepareCite(node) ? node : original(node);
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
  "mezhs-writing-block": (props: RichProps) => <RichWritingBlock
    source={read(props, "writing-source")}
    variant={read(props, "variant") || "standard"}
    subject={read(props, "subject")}
    recipient={read(props, "recipient")}
    cc={read(props, "cc")}
    bcc={read(props, "bcc")}
  />,
  "mezhs-cite": Cite,
} as Components;
