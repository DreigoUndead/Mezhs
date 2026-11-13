import { RichDocumentView, type RichDocument } from "./RichDocumentView";
import Markdown, { uriTransformer } from "react-markdown";
import remarkGfm from "remark-gfm";
import { RichActionContext } from "./RichActionContext";
import "./markdown.css";

const safeUri = (value: string) => {
  const validated = uriTransformer(value);
  return validated === "javascript:void(0)" ? "" : validated;
};

// Only canonical server-authored AST nodes can create rich UI actions.
// Streaming, local and legacy replies without a document remain inert Markdown.
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
  return <div className="markdown-content">
    <Markdown remarkPlugins={[remarkGfm]} transformLinkUri={safeUri} transformImageUri={() => ""}>
      {content}
    </Markdown>
  </div>;
}
