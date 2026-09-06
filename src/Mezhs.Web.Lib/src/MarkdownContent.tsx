import { Fragment, ReactNode } from "react";
import "./markdown.css";

type MarkdownContentProps = {
  content: string;
};

type MarkdownBlock =
  | { kind: "code"; language?: string; content: string }
  | { kind: "heading"; level: number; content: string }
  | { kind: "quote"; content: string }
  | { kind: "unordered-list"; items: string[] }
  | { kind: "ordered-list"; items: string[] }
  | { kind: "paragraph"; content: string };

const inlineToken = /(`[^`\n]+`|\*\*[^*\n]+\*\*|\*[^*\n]+\*|\[[^\]\n]+\]\([^\s)]+\))/g;

function safeLink(value: string) {
  return /^(https?:|mailto:)/i.test(value) ? value : null;
}

function renderInline(value: string): ReactNode[] {
  const result: ReactNode[] = [];
  let cursor = 0;
  let key = 0;

  for (const match of value.matchAll(inlineToken)) {
    const index = match.index ?? 0;
    if (index > cursor) result.push(value.slice(cursor, index));

    const token = match[0];
    if (token.startsWith("`")) {
      result.push(<code key={key++}>{token.slice(1, -1)}</code>);
    } else if (token.startsWith("**")) {
      result.push(<strong key={key++}>{token.slice(2, -2)}</strong>);
    } else if (token.startsWith("*")) {
      result.push(<em key={key++}>{token.slice(1, -1)}</em>);
    } else {
      const link = token.match(/^\[([^\]]+)]\(([^)]+)\)$/);
      const href = link ? safeLink(link[2]) : null;
      result.push(href
        ? <a key={key++} href={href} target="_blank" rel="noreferrer">{link![1]}</a>
        : <Fragment key={key++}>{link?.[1] ?? token}</Fragment>);
    }
    cursor = index + token.length;
  }

  if (cursor < value.length) result.push(value.slice(cursor));
  return result;
}

function parseBlocks(content: string): MarkdownBlock[] {
  const lines = content.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
  const blocks: MarkdownBlock[] = [];

  for (let index = 0; index < lines.length;) {
    const line = lines[index];
    if (!line.trim()) {
      index++;
      continue;
    }

    const fence = line.match(/^\s*```([^`]*)$/);
    if (fence) {
      const body: string[] = [];
      index++;
      while (index < lines.length && !/^\s*```\s*$/.test(lines[index])) body.push(lines[index++]);
      if (index < lines.length) index++;
      blocks.push({ kind: "code", language: fence[1].trim() || undefined, content: body.join("\n") });
      continue;
    }

    const heading = line.match(/^\s*(#{1,6})\s+(.+)$/);
    if (heading) {
      blocks.push({ kind: "heading", level: heading[1].length, content: heading[2].trim() });
      index++;
      continue;
    }

    if (/^\s*>\s?/.test(line)) {
      const body: string[] = [];
      while (index < lines.length && /^\s*>\s?/.test(lines[index]))
        body.push(lines[index++].replace(/^\s*>\s?/, ""));
      blocks.push({ kind: "quote", content: body.join("\n") });
      continue;
    }

    const unordered = line.match(/^\s*[-*+]\s+(.+)$/);
    if (unordered) {
      const items: string[] = [];
      while (index < lines.length) {
        const item = lines[index].match(/^\s*[-*+]\s+(.+)$/);
        if (!item) break;
        items.push(item[1]);
        index++;
      }
      blocks.push({ kind: "unordered-list", items });
      continue;
    }

    const ordered = line.match(/^\s*\d+[.)]\s+(.+)$/);
    if (ordered) {
      const items: string[] = [];
      while (index < lines.length) {
        const item = lines[index].match(/^\s*\d+[.)]\s+(.+)$/);
        if (!item) break;
        items.push(item[1]);
        index++;
      }
      blocks.push({ kind: "ordered-list", items });
      continue;
    }

    const paragraph: string[] = [];
    while (index < lines.length && lines[index].trim()) {
      if (paragraph.length > 0 && (/^\s*```/.test(lines[index]) || /^\s*#{1,6}\s+/.test(lines[index]) || /^\s*>\s?/.test(lines[index]) || /^\s*[-*+]\s+/.test(lines[index]) || /^\s*\d+[.)]\s+/.test(lines[index]))) break;
      paragraph.push(lines[index++].trim());
    }
    blocks.push({ kind: "paragraph", content: paragraph.join(" ") });
  }

  return blocks;
}

function Heading({ level, children }: { level: number; children: ReactNode }) {
  switch (level) {
    case 1: return <h1>{children}</h1>;
    case 2: return <h2>{children}</h2>;
    case 3: return <h3>{children}</h3>;
    case 4: return <h4>{children}</h4>;
    case 5: return <h5>{children}</h5>;
    default: return <h6>{children}</h6>;
  }
}

export function MarkdownContent({ content }: MarkdownContentProps) {
  return (
    <div className="markdown-content">
      {parseBlocks(content).map((block, index) => {
        switch (block.kind) {
          case "code":
            return <pre key={index}><code data-language={block.language}>{block.content}</code></pre>;
          case "heading":
            return <Heading key={index} level={block.level}>{renderInline(block.content)}</Heading>;
          case "quote":
            return <blockquote key={index}>{block.content.split("\n").map((line, lineIndex) => <Fragment key={lineIndex}>{renderInline(line)}{lineIndex < block.content.split("\n").length - 1 && <br />}</Fragment>)}</blockquote>;
          case "unordered-list":
            return <ul key={index}>{block.items.map((item, itemIndex) => <li key={itemIndex}>{renderInline(item)}</li>)}</ul>;
          case "ordered-list":
            return <ol key={index}>{block.items.map((item, itemIndex) => <li key={itemIndex}>{renderInline(item)}</li>)}</ol>;
          case "paragraph":
            return <p key={index}>{renderInline(block.content)}</p>;
        }
      })}
    </div>
  );
}
