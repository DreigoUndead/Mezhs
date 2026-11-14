import { RichDocumentView, type RichDocument } from "./RichDocumentView";
import { RichActionContext } from "./RichActionContext";
import "./markdown.css";

// The canonical C# document is the only Markdown / rich syntax authority.
// Provisional, streaming, and legacy content without that document is escaped
// plain source text; React never parses it through another Markdown grammar.
export function MarkdownContent({ content, document, verbatim = false, onIssueNewTurn }: {
  content: string;
  document?: RichDocument | null;
  verbatim?: boolean;
  onIssueNewTurn?: (content: string) => void;
}) {
  if (verbatim) return <div className="prompt-text">{content}</div>;
  if (document) return <RichActionContext.Provider value={onIssueNewTurn}>
    <RichDocumentView document={document} source={content} />
  </RichActionContext.Provider>;
  return <div className="markdown-content prompt-text">{content}</div>;
}
