import { fromMarkdown } from "mdast-util-from-markdown";
type MarkdownNode = {
  type: string;
  children?: MarkdownNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
};

// CommonMark AST locations establish which source ranges are code examples.
// This covers indented blocks and fences nested in lists/quotes without a
// separate hand-written Markdown scanner. No model code is ever evaluated.
export function protectedPositions(source: string): Uint8Array | null {
  try {
    // Model content can mix LF and CRLF (notably after Windows fixture or
    // transport transformations). Normalize only the syntax prepass and map
    // offsets back to the exact original source without rewriting its text.
    let syntax = source;
    let originalOffsets: number[] | undefined;
    if (source.includes("\r")) {
      const chars: string[] = [];
      originalOffsets = [];
      for (let i = 0; i < source.length;) {
        originalOffsets.push(i);
        if (source[i] === "\r") {
          chars.push("\n");
          i += source[i + 1] === "\n" ? 2 : 1;
        } else chars.push(source[i++]);
      }
      originalOffsets.push(source.length);
      syntax = chars.join("");
    }
    const mask = new Uint8Array(source.length);
    const nodes: MarkdownNode[] = [fromMarkdown(syntax) as unknown as MarkdownNode];
    while (nodes.length) {
      const node = nodes.pop()!;
      if (node.type === "code" || node.type === "inlineCode") {
        const from = node.position?.start.offset;
        const to = node.position?.end.offset;
        if (from === undefined || to === undefined || from < 0 ||
            to > syntax.length || to <= from) return null;
        const start = originalOffsets ? originalOffsets[from] : from;
        const end = originalOffsets ? originalOffsets[to] : to;
        mask.fill(1, start, end);
      } else if (node.children) {
        for (let i = node.children.length - 1; i >= 0; i--) nodes.push(node.children[i]);
      }
    }
    // Conservative backup for source fences when container/list parsing
    // yields only separate marker-line code nodes. Never grant authority to
    // something bracketed by a Markdown-style fence, even if AST contexts
    // disagree. The strict closer rule preserves fenced examples with faux
    // closing markers; runtime and allocations remain bounded by source size.
    let offset = 0;
    let fence: { from: number; marker: string } | null = null;
    for (const line of source.match(/[^\n]*(?:\n|$)/g) || []) {
      if (!line) continue;
      if (fence) {
        const closing = /^ {0,3}(`{3,}|~{3,})[ \t]*(?:\r?\n)?$/.exec(line)?.[1];
        if (closing?.[0] === fence.marker[0] && closing.length >= fence.marker.length) {
          mask.fill(1, fence.from, offset + line.length);
          fence = null;
        }
      } else {
        const opening = /^ {0,3}(`{3,}|~{3,})/.exec(line)?.[1];
        if (opening) fence = { from: offset, marker: opening };
      }
      offset += line.length;
    }
    if (fence) mask.fill(1, fence.from);
    return mask;
  } catch {
    // A failed syntax prepass cannot grant action authority.
    return null;
  }
}


