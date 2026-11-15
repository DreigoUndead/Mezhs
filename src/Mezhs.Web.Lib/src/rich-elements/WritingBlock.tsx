import { useEffect, useState } from "react";

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
