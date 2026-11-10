import { createContext, useContext, useEffect, useState } from "react";
import { protectedPositions } from "./RichMarkdownProtection";
import type { MarkupNode } from "./rich-elements/Element";

const variants: Record<string, string> = {
  email: "Email draft",
  chat_message: "Chat message",
  social_post: "Social post",
  document: "Document",
  standard: "Draft",
};

export function RichWritingBlock({ source, variant, subject, recipient, cc, bcc }: {
  source: string;
  variant: string;
  subject: string;
  recipient: string;
  cc: string;
  bcc: string;
}) {
  const [draft, setDraft] = useState(source);
  const [copyStatus, setCopyStatus] = useState("");
  useEffect(() => { setDraft(source); setCopyStatus(""); }, [source]);

  const copy = async () => {
    try {
      if (typeof navigator === "undefined" || !navigator.clipboard?.writeText)
        throw new Error("Clipboard unavailable");
      await navigator.clipboard.writeText(draft);
      setCopyStatus("Copied");
    } catch {
      setCopyStatus("Copy unavailable. Select the text to copy manually.");
    }
  };

  return <section className="rich-writing-block" aria-label={variants[variant] || "Draft"}>
    <div className="rich-writing-header">
      <strong>{variants[variant] || "Draft"}</strong>
      <button type="button" onClick={copy} aria-label="Copy writing block">Copy</button>
    </div>
    {(subject || recipient || cc || bcc) && <div className="rich-writing-metadata">
      {recipient && <div><strong>To:</strong> {recipient}</div>}
      {cc && <div><strong>Cc:</strong> {cc}</div>}
      {bcc && <div><strong>Bcc:</strong> {bcc}</div>}
      {subject && <div><strong>Subject:</strong> {subject}</div>}
    </div>}
    <textarea aria-label="Edit writing block" value={draft} onChange={event => {
      setDraft(event.target.value);
      setCopyStatus("");
    }} rows={Math.max(5, Math.min(18, draft.split("\n").length + 2))} spellCheck />
    {copyStatus && <span className="rich-writing-status" role="status">{copyStatus}</span>}
  </section>;
}


export type WritingBlockData = {
  source: string; variant: string; subject: string;
  recipient: string; cc: string; bcc: string;
};

export const WritingBlocksContext =
  createContext<Record<string, WritingBlockData>>(Object.create(null));

export function RichWritingBlockSlot(props: Record<string, unknown>) {
  const blocks = useContext(WritingBlocksContext);
  const id = props["data-mezhs-writing-id"];
  const block = typeof id === "string" &&
    Object.prototype.hasOwnProperty.call(blocks, id) ? blocks[id] : undefined;
  return block ? <RichWritingBlock {...block} /> : null;
}

function openingMetadata(raw: string): Omit<WritingBlockData, "source"> | null {
  if (!raw.startsWith("<WritingBlock") || !raw.endsWith(">")) return null;
  const attrs: Record<string, string> = Object.create(null);
  let rest = raw.slice("<WritingBlock".length, -1);
  const allowed = new Set(["variant", "subject", "recipient", "cc", "bcc", "id"]);
  while (rest.trim()) {
    const match = /^\s+([a-zA-Z][a-zA-Z0-9]*)\s*=\s*(?:"([^"\n\r]*)"|'([^'\n\r]*)')/.exec(rest);
    if (!match || !allowed.has(match[1]) || Object.prototype.hasOwnProperty.call(attrs, match[1]))
      return null;
    const value = match[2] ?? match[3];
    if (value.length > 256) return null;
    attrs[match[1]] = value;
    rest = rest.slice(match[0].length);
  }
  if (attrs.variant && !["email", "chat_message", "social_post", "document", "standard"].includes(attrs.variant))
    return null;
  if (attrs.id && !/^[0-9]{5}$/.test(attrs.id)) return null;
  return {
    variant: attrs.variant || "standard", subject: attrs.subject || "",
    recipient: attrs.recipient || "", cc: attrs.cc || "", bcc: attrs.bcc || "",
  };
}

// Before the MDX/template parsers run, shield a complete standalone
// WritingBlock. Its raw Markdown can contain otherwise invalid MDX source.
export function extractWritingBlocks(source: string): {
  content: string; blocks: Record<string, WritingBlockData>;
} {
  const blocks: Record<string, WritingBlockData> = Object.create(null);
  if (!source.includes("<WritingBlock") || source.length > 180000)
    return { content: source, blocks };
  const mask = protectedPositions(source);
  if (!mask) return { content: source, blocks };
  // HTML comments cannot create interactive source editing surfaces.
  for (const comment of source.matchAll(/<!--[\s\S]*?(?:-->|$)/g))
    mask.fill(1, comment.index!, comment.index! + comment[0].length);
  const replacements: Array<{ start: number; end: number; value: string }> = [];
  const opener = /^ {0,3}<WritingBlock(?=[\s>])/gm;
  const closer = /<\/WritingBlock\s*>/g;
  let consumed = 0;
  for (const match of source.matchAll(opener)) {
    const start = match.index!;
    if (start < consumed || mask[start] || replacements.length >= 16) continue;
    // Only the opener attributes are syntax; the body is preserved verbatim.
    let quote = "";
    let openEnd = -1;
    for (let i = start + match[0].length; i < source.length && i - start < 1600; i++) {
      const char = source[i];
      if (char === "\n" || char === "\r") break;
      if (quote) { if (char === quote) quote = ""; }
      else if (char === '"' || char === "'") quote = char;
      else if (char === ">") { openEnd = i + 1; break; }
    }
    if (openEnd < 0) continue;
    const metadata = openingMetadata(source.slice(start, openEnd).trimStart());
    if (!metadata) continue;
    closer.lastIndex = openEnd;
    let close: RegExpExecArray | null;
    while ((close = closer.exec(source)) && mask[close.index]) { /* skip quoted code */ }
    if (!close) continue;
    const end = close.index + close[0].length;
    if (end - start > 32768) continue;
    const body = source.slice(openEnd, close.index).replace(/^\r?\n|\r?\n$/g, "");
    if (!body.trim() || body.length > 24000) continue;
    const id = "w" + replacements.length;
    blocks[id] = { ...metadata, source: body };
    replacements.push({ start, end, value: '<mezhs-writing-slot id="' + id + '" />' });
    consumed = end;
  }
  let output = "";
  let cursor = 0;
  for (const replacement of replacements) {
    output += source.slice(cursor, replacement.start) + replacement.value;
    cursor = replacement.end;
  }
  return { content: output + source.slice(cursor), blocks };
}

// Check the exact post-expansion source location, never text alone. A fake
// model-authored slot with the same id makes that id ineligible.
export function writingSlotOffsets(content: string, blocks: Record<string, WritingBlockData>) {
  const offsets: Record<string, number> = Object.create(null);
  for (const id of Object.keys(blocks)) {
    const marker = '<mezhs-writing-slot id="' + id + '" />';
    const at = content.indexOf(marker);
    if (at >= 0 && content.indexOf(marker, at + marker.length) === -1)
      offsets[id] = at;
  }
  return offsets;
}
