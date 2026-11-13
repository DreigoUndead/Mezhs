import { useEffect, useState } from "react";

export type CodeBlockData = {
  source: string;
  language: string;
  editable: boolean;
};

export function RichCodeBlock({ source, language, editable }: CodeBlockData) {
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
      setCopyStatus("Copy unavailable. Select the code to copy manually.");
    }
  };

  return <section className="rich-code-block" aria-label={language + " code block"}>
    <div className="rich-code-header">
      <span>{language}</span>
      <button type="button" onClick={copy} aria-label="Copy code block">Copy</button>
    </div>
    {editable
      ? <textarea aria-label="Edit code block" value={draft} spellCheck={false}
          onChange={event => { setDraft(event.target.value); setCopyStatus(""); }}
          rows={Math.max(5, Math.min(20, draft.split("\n").length + 1))} />
      : <pre><code>{source}</code></pre>}
    {copyStatus && <span role="status" className="rich-code-status">{copyStatus}</span>}
  </section>;
}
