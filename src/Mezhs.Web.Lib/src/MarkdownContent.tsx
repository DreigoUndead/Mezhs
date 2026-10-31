import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeRaw from "rehype-raw";
import rehypeSanitize from "rehype-sanitize";
import "./markdown.css";

// Provider DOM markup is parsed and sanitized as data; it is never executed.
export function MarkdownContent({ content, renderedHtml }: { content: string; renderedHtml?: string | null }) {
  return (
    <div className="markdown-content">
      {renderedHtml
        ? <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw, rehypeSanitize]}>{renderedHtml}</Markdown>
        : <Markdown remarkPlugins={[remarkGfm]}>{content}</Markdown>}
    </div>
  );
}
