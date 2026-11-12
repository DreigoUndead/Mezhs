import type { Components } from "react-markdown";
import { prepareSvg, svgComponents } from "./RichSvg";
import { RichWritingBlockSlot } from "./RichWritingBlock";
import { RichCodeBlockSlot } from "./RichCodeBlock";
import { prepareCite, Cite } from "./rich-elements/Cite";
import { richElementRegistry, richStaticComponents } from "./rich-elements";
import { prepareStaticElement, type MarkupNode } from "./rich-elements/Element";

// An unsupported parent must fall back to the author's literal source,
// not expose host-generated slot markers or discard the extracted body.
// Only markers at validated full-message offsets can be restored; a
// model-supplied marker is never allowed to impersonate an extracted block.
export function restoreRawSlots(
  source: string, start: number, end: number,
  writingOffsets: Record<string, number>, codeOffsets: Record<string, number>,
  rawSources: Record<string, string>
): string {
  const replacements: Array<{ start: number; end: number; text: string }> = [];
  for (const [kind, offsets] of [["writing", writingOffsets], ["code", codeOffsets]] as const) {
    for (const [id, offset] of Object.entries(offsets)) {
      const marker = '<mezhs-' + kind + '-slot id="' + id + '" />';
      const key = kind + ":" + id;
      if (offset < start || offset + marker.length > end ||
          !source.startsWith(marker, offset) ||
          !Object.prototype.hasOwnProperty.call(rawSources, key)) continue;
      replacements.push({ start: offset, end: offset + marker.length, text: rawSources[key] });
    }
  }
  replacements.sort((a, b) => a.start - b.start);
  let result = "";
  let cursor = start;
  for (const replacement of replacements) {
    result += source.slice(cursor, replacement.start) + replacement.text;
    cursor = replacement.end;
  }
  return result + source.slice(cursor, end);
}

// Fixed host-owned dispatch. No MDX compilation, model JS or dynamic imports.
export function remarkRichMarkup(slotOffsets: Record<string, number> = Object.create(null),
  writingOffsets: Record<string, number> = Object.create(null),
  codeOffsets: Record<string, number> = Object.create(null),
  rawSources: Record<string, string> = Object.create(null)) {
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
      return { type: "text", value: restoreRawSlots(source, start, end, writingOffsets, codeOffsets, rawSources) };
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
