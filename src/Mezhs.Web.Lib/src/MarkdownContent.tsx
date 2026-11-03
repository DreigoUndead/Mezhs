import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import remarkMdx from "remark-mdx";
import { remarkRichMarkup, richComponents } from "./RichMarkup";
import { expandRichTemplates } from "./RichExpression";
import "./markdown.css";

// One renderer for every integration. Provider content is parsed as data;
// model-supplied markup and expressions are never compiled or executed.
function renderRich(content: string) {
  return Markdown({
    children: expandRichTemplates(content),
    remarkPlugins: [remarkGfm, remarkMdx as unknown as typeof remarkGfm, remarkRichMarkup],
    components: richComponents,
  });
}

function renderChunk(content: string) {
  try {
    return renderRich(content);
  } catch {
    return <Markdown remarkPlugins={[remarkGfm]}>{content}</Markdown>;
  }
}

export function MarkdownContent({ content, verbatim = false }: { content: string; verbatim?: boolean }) {
  if (verbatim) return <div className="prompt-text">{content}</div>;
  try {
    return <div className="markdown-content">{renderRich(content)}</div>;
  } catch {
    // One malformed block must not disable unrelated rich blocks.
    return <div className="markdown-content">{content.split(/\n\s*\n/).map((chunk, i) =>
      <div key={i}>{renderChunk(chunk)}</div>
    )}</div>;
  }
}
