import { createContext, useContext, useEffect, useState } from "react";

export type CodeBlockData = {
  source: string;
  language: string;
  editable: boolean;
};

// Only literal metadata is accepted. No model-authored functions or HTML.
export function codeBlockMetadata(raw: string): Omit<CodeBlockData, "source"> | null {
  if (!raw.startsWith("<CodeBlock") || !raw.endsWith(">")) return null;
  const attributes: Record<string, string> = Object.create(null);
  let remaining = raw.slice("<CodeBlock".length, -1);
  while (remaining.trim()) {
    const match = /^\s+(language|editable)(?=\s|=|$)(?:\s*=\s*(?:"([^"\r\n]*)"|'([^'\r\n]*)'|\{(true|false)\}))?/.exec(remaining);
    if (!match || Object.prototype.hasOwnProperty.call(attributes, match[1])) return null;
    const name = match[1];
    const value = match[2] ?? match[3] ?? match[4];
    if (name === "language") {
      if (!value || !/^[a-z0-9_+#.-]{1,32}$/i.test(value)) return null;
      attributes.language = value;
    } else {
      if (value !== undefined && value !== "true" && value !== "false") return null;
      attributes.editable = value ?? "true";
    }
    remaining = remaining.slice(match[0].length);
  }
  return { language: attributes.language || "text", editable: attributes.editable === "true" };
}

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

export const CodeBlocksContext = createContext<Record<string, CodeBlockData>>(Object.create(null));

export function RichCodeBlockSlot(props: Record<string, unknown>) {
  const blocks = useContext(CodeBlocksContext);
  const id = props["data-mezhs-code-id"];
  const block = typeof id === "string" &&
    Object.prototype.hasOwnProperty.call(blocks, id) ? blocks[id] : undefined;
  return block ? <RichCodeBlock {...block} /> : null;
}
