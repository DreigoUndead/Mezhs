import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import "./markdown.css";

export function MarkdownContent({ content }: { content: string }) {
  return <div className="markdown-content"><Markdown remarkPlugins={[remarkGfm]}>{content}</Markdown></div>;
}
