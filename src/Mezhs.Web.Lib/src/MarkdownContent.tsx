import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import remarkMdx from "remark-mdx";
import { remarkStaticUi, uiComponents } from "./StaticMarkup";
import "./markdown.css";

type MarkdownContentProps = { content: string };

// The model's MDX-shaped layout tags are parsed as data, not compiled to JS.
// Invalid/incomplete JSX (including streaming responses) falls back to normal
// safe Markdown rather than crashing the transcript.
export function MarkdownContent({ content }: MarkdownContentProps) {
  let rendered;
  try {
    rendered = Markdown({
      children: content,
      remarkPlugins: [remarkGfm, remarkMdx as unknown as typeof remarkGfm, remarkStaticUi],
      components: uiComponents,
    });
  } catch {
    rendered = Markdown({ children: content, remarkPlugins: [remarkGfm] });
  }
  return <div className="markdown-content">{rendered}</div>;
}
