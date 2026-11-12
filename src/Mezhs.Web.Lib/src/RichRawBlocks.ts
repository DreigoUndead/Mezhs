import { protectedPositions } from "./RichMarkdownProtection";
import { codeBlockMetadata, type CodeBlockData } from "./RichCodeBlock";
import { writingBlockMetadata, type WritingBlockData } from "./RichWritingBlock";

type RawBlockKind = "CodeBlock" | "WritingBlock";
type Replacement = { start: number; end: number; text: string };

export function extractRawBlocks(source: string): {
  content: string;
  writingBlocks: Record<string, WritingBlockData>;
  codeBlocks: Record<string, CodeBlockData>;
  rawSources: Record<string, string>;
} {
  const writingBlocks: Record<string, WritingBlockData> = Object.create(null);
  const codeBlocks: Record<string, CodeBlockData> = Object.create(null);
  const rawSources: Record<string, string> = Object.create(null);
  const unchanged = () => ({ content: source, writingBlocks, codeBlocks, rawSources });
  if (source.length > 180000 || (!source.includes("<WritingBlock") && !source.includes("<CodeBlock")))
    return unchanged();

  // This Markdown prepass is for locating code examples, never for executing MDX.
  const mask = protectedPositions(source);
  if (!mask) return unchanged();
  // A comment marker shown inside Markdown code is literal source, not the
  // start of an HTML comment covering subsequent rich controls. Match both
  // endpoints outside pre-protected code, preserving that code's original mask.
  for (let i = 0; i < source.length;) {
    if (mask[i] || !source.startsWith("<!--", i)) {
      i++;
      continue;
    }
    const start = i;
    i += 4;
    while (i < source.length && (mask[i] || !source.startsWith("-->", i)))
      i++;
    i = Math.min(source.length, i + 3);
    mask.fill(1, start, i);
  }

  const replacements: Replacement[] = [];
  const opener = /^ {0,3}<(WritingBlock|CodeBlock)(?=[\s>])/gm;
  let consumed = 0;
  let inspected = 0;
  for (const match of source.matchAll(opener)) {
    if (++inspected > 48 || replacements.length >= 16) break;
    const start = match.index!;
    if (start < consumed || mask[start]) continue;
    const kind = match[1] as RawBlockKind;
    let quote = "";
    let openEnd = -1;
    for (let i = start + match[0].length; i < source.length && i - start < 1600; i++) {
      const char = source[i];
      if (char === "\n" || char === "\r") break;
      if (quote) {
        if (char === quote) quote = "";
      } else if (char === '"' || char === "'") {
        quote = char;
      } else if (char === ">") {
        openEnd = i + 1;
        break;
      }
    }
    if (openEnd < 0) continue;
    const opening = source.slice(start, openEnd).trimStart();
    const metadata = kind === "CodeBlock" ? codeBlockMetadata(opening) : writingBlockMetadata(opening);
    if (!metadata) continue;

    // CodeBlock has a standalone closing delimiter. The source may contain
    // malformed Markdown or JSX; do not parse its body as executable syntax.
    // Both controls use a standalone closing delimiter. WritingBlock also
    // excludes closers inside Markdown code/comments through the source mask.
    const closer = kind === "CodeBlock"
      ? /^ {0,3}<\/CodeBlock\s*>[ \t]*$/gm
      : /^ {0,3}<\/WritingBlock\s*>[ \t]*$/gm;
    closer.lastIndex = openEnd;
    let close: RegExpExecArray | null = null;
    while ((close = closer.exec(source))) {
      if (kind === "CodeBlock" || !mask[close.index]) break;
    }
    if (!close) continue;
    const end = close.index + close[0].length;
    if (end - start > 32768) continue;
    const body = source.slice(openEnd, close.index).replace(/^\r?\n|\r?\n$/g, "");
    if (!body.trim() || body.length > 24000) continue;

    if (kind === "CodeBlock") {
      const id = "c" + Object.keys(codeBlocks).length;
      codeBlocks[id] = { ...(metadata as Omit<CodeBlockData, "source">), source: body };
      rawSources["code:" + id] = source.slice(start, end);
      replacements.push({ start, end, text: '<mezhs-code-slot id="' + id + '" />' });
    } else {
      const id = "w" + Object.keys(writingBlocks).length;
      writingBlocks[id] = { ...(metadata as Omit<WritingBlockData, "source">), source: body };
      rawSources["writing:" + id] = source.slice(start, end);
      replacements.push({ start, end, text: '<mezhs-writing-slot id="' + id + '" />' });
    }
    consumed = end;
  }
  let content = "";
  let cursor = 0;
  for (const replacement of replacements) {
    content += source.slice(cursor, replacement.start) + replacement.text;
    cursor = replacement.end;
  }
  return { content: content + source.slice(cursor), writingBlocks, codeBlocks, rawSources };
}

// Slots are host-generated. A forged or duplicate marker cannot gain access
// to the protected raw content, even after rich-template/question extraction.
export function rawSlotOffsets(content: string, blocks: Record<string, unknown>, kind: "code" | "writing") {
  const offsets: Record<string, number> = Object.create(null);
  for (const id of Object.keys(blocks)) {
    const marker = '<mezhs-' + kind + '-slot id="' + id + '" />';
    const index = content.indexOf(marker);
    if (index >= 0 && content.indexOf(marker, index + marker.length) === -1)
      offsets[id] = index;
  }
  return offsets;
}
