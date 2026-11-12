import { createContext, useContext, useEffect, useState } from "react";

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

export function writingBlockMetadata(raw: string): Omit<WritingBlockData, "source"> | null {
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
