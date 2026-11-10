import { useEffect, useState } from "react";
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

export function prepareWritingBlock(node: MarkupNode, source: string): boolean {
            // Extract the original Markdown source, not rendered children.
            // This component is local editing/copy only; no host action authority.
            const attrs: Record<string, string> = Object.create(null);
            const allowed = new Set(["variant", "subject", "recipient", "cc", "bcc", "id"]);
            let safe = (node.attributes || []).length <= 6;
            for (const attr of node.attributes || []) {
              if (attr.type !== "mdxJsxAttribute" || !attr.name ||
                  !allowed.has(attr.name) || Object.prototype.hasOwnProperty.call(attrs, attr.name) ||
                  typeof attr.value !== "string" || attr.value.length > 256) { safe = false; break; }
              attrs[attr.name] = attr.value;
            }
            if (attrs.variant && !["email", "chat_message", "social_post", "document", "standard"].includes(attrs.variant))
              safe = false;
            if (attrs.id && !/^[0-9]{5}$/.test(attrs.id)) safe = false;
            const start = node.position?.start.offset;
            const end = node.position?.end.offset;
            const raw = start !== undefined && end !== undefined && end - start <= 32768
              ? source.slice(start, end) : "";
            // The opening tag can contain > inside a quoted metadata value.
            let openerEnd = -1;
            let quote = "";
            for (let i = 0; i < Math.min(raw.length, 1600); i++) {
              const char = raw[i];
              if (quote) { if (char === quote) quote = ""; }
              else if (char === '"' || char === "'") quote = char;
              else if (char === ">") { openerEnd = i + 1; break; }
            }
            const closer = /<\/WritingBlock\s*>$/.exec(raw);
            const body = closer && openerEnd > 0 ? raw.slice(openerEnd, closer.index).replace(/^\n|\n$/g, "") : "";
            if (!safe || !raw.startsWith("<WritingBlock") || !closer ||
                openerEnd < 0 || body.length > 24000 || !body.trim()) return false;
            node.children = [];
            node.data = { hName: "mezhs-writing-block", hProperties: {
              "data-mezhs-writing-source": body,
              ...Object.fromEntries(Object.entries(attrs).map(([key, value]) => ["data-mezhs-" + key, value])),
            } };
            return true;

}
