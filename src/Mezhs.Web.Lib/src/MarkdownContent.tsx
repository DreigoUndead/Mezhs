import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeRaw from "rehype-raw";
import rehypeSanitize from "rehype-sanitize";
import remarkMdx from "remark-mdx";
import { remarkRichMarkup, richComponents, expandRichTemplates } from "./RichMarkup";
import "./markdown.css";

// Provider DOM markup is parsed and sanitized as data; it is never executed.
export function MarkdownContent({ content, renderedHtml }: { content: string; renderedHtml?: string | null }) {
  return (
    <div className="markdown-content">
      {renderedHtml
        ? <>
            <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw, rehypeSanitize]}>{renderedHtml}</Markdown>
            {content && <details className="rich-text-fallback">
              <summary>Show original text</summary>
              <Markdown remarkPlugins={[remarkGfm]}>{content}</Markdown>
            </details>}
          </>
        : <RichMarkdown content={content} />}
    </div>
  );
}

function RichMarkdown({ content }: { content: string }) {
  // JSX syntax is parsed into inert AST nodes; model expressions are never evaluated.
  // Incomplete markup (including an in-progress reply) remains readable as Markdown.
  try {
    return Markdown({
      children: expandRichTemplates(content),
      remarkPlugins: [remarkGfm, remarkMdx as unknown as typeof remarkGfm, remarkRichMarkup],
      components: richComponents,
    });
  } catch {
    return <Markdown remarkPlugins={[remarkGfm]}>{content}</Markdown>;
  }
}
