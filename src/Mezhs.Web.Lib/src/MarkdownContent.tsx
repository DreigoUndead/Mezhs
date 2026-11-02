import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeRaw from "rehype-raw";
import rehypeSanitize from "rehype-sanitize";
import remarkMdx from "remark-mdx";
import { remarkRichMarkup, richComponents } from "./RichMarkup";
import { expandRichTemplates } from "./RichExpression";
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

function renderRichChunk(content: string) {
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

function RichMarkdown({ content }: { content: string }) {
  try {
    return Markdown({
      children: expandRichTemplates(content),
      remarkPlugins: [remarkGfm, remarkMdx as unknown as typeof remarkGfm, remarkRichMarkup],
      components: richComponents,
    });
  } catch {
    // A malformed or unsupported block should not disable earlier rich blocks.
    // Recovery is paragraph-scoped; the original text stays visible for failures.
    return <>{content.split(/\n\s*\n/).map((chunk, i) =>
      <div key={i}>{renderRichChunk(chunk)}</div>
    )}</>;
  }
}
