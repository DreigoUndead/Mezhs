import { useMemo } from "react";
import Markdown, { uriTransformer } from "react-markdown";
import type { Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import remarkMdx from "remark-mdx";
import { remarkRichMarkup, richComponents } from "./RichMarkup";
import { expandRichTemplates } from "./RichExpression";
import { extractRichQuestions, RichQuestionSlot, RichQuestionsContext, RichQuestionData } from "./RichQuestion";
import { RichActionContext } from "./RichActionContext";
import { extractWritingBlocks, writingSlotOffsets, WritingBlocksContext } from "./RichWritingBlock";
import "./markdown.css";

// Keep react-markdown protocol validation without emitting a javascript: placeholder.
const safeUri = (value: string) => {
  const validated = uriTransformer(value);
  return validated === "javascript:void(0)" ? "" : validated;
};

// One provider-neutral renderer. All model syntax is parsed as data, never
// compiled or evaluated. Only a real host callback grants send authority.
const components = { ...richComponents, "mezhs-question": RichQuestionSlot } as Components;

function renderRich(content: string, slotOffsets: Record<string, number>,
  writingOffsets: Record<string, number>) {
  const parseMarkup = () => remarkRichMarkup(slotOffsets, writingOffsets);
  return Markdown({
    children: content,
    remarkPlugins: [remarkGfm, remarkMdx as unknown as typeof remarkGfm, parseMarkup as unknown as typeof remarkGfm],
    components,
    transformLinkUri: safeUri,
    transformImageUri: safeUri,
  });
}

function renderChunk(content: string, slotOffsets: Record<string, number>,
  writingOffsets: Record<string, number>) {
  try {
    // Only action slots provenanced from full-message parsing are eligible.
    // Never extract new questions from a recovery fragment: the original
    // Markdown fence/indentation context may no longer be present.
    return renderRich(content, slotOffsets, writingOffsets);
  } catch {
    return <Markdown remarkPlugins={[remarkGfm]} transformLinkUri={safeUri} transformImageUri={safeUri}>{content}</Markdown>;
  }
}

function recoveryChunks(content: string, slotOffsets: Record<string, number>,
  writingOffsets: Record<string, number>) {
  const chunks: Array<{ content: string; slotOffsets: Record<string, number>; writingOffsets: Record<string, number> }> = [];
  let start = 0;
  const append = (end: number) => {
    const localOffsets: Record<string, number> = Object.create(null);
    for (const [id, offset] of Object.entries(slotOffsets)) {
      if (offset >= start && offset < end) localOffsets[id] = offset - start;
    }
    const localWriting: Record<string, number> = Object.create(null);
    for (const [id, offset] of Object.entries(writingOffsets)) {
      if (offset >= start && offset < end) localWriting[id] = offset - start;
    }
    chunks.push({ content: content.slice(start, end), slotOffsets: localOffsets, writingOffsets: localWriting });
  };
  for (const match of content.matchAll(/\n\s*\n/g)) {
    append(match.index!);
    start = match.index! + match[0].length;
  }
  append(content.length);
  return chunks;
}

export function MarkdownContent({ content, verbatim = false, onIssueNewTurn }: {
  content: string;
  verbatim?: boolean;
  onIssueNewTurn?: (content: string) => void;
}) {
  const prepared = useMemo(() => {
    if (verbatim) return null;
    const writing = extractWritingBlocks(content);
    const questions = extractRichQuestions(expandRichTemplates(writing.content));
    return { ...questions, blocks: writing.blocks,
      writingOffsets: writingSlotOffsets(questions.content, writing.blocks) };
  }, [content, verbatim]);
  if (verbatim) return <div className="prompt-text">{content}</div>;
  try {
    if (!prepared) return null;
    return <WritingBlocksContext.Provider value={prepared.blocks}>
      <RichActionContext.Provider value={onIssueNewTurn}>
      <RichQuestionsContext.Provider value={prepared.questions}>
        <div className="markdown-content">{renderRich(prepared.content, prepared.slotOffsets, prepared.writingOffsets)}</div>
      </RichQuestionsContext.Provider>
      </RichActionContext.Provider>
    </WritingBlocksContext.Provider>;
  } catch {
    // Recover independently renderable blocks using only the complete
    // message's already-validated slots. No fragment can gain new authority.
    if (!prepared) return null;
    return <WritingBlocksContext.Provider value={prepared.blocks}>
      <RichActionContext.Provider value={onIssueNewTurn}>
      <RichQuestionsContext.Provider value={prepared.questions}>
        <div className="markdown-content">{recoveryChunks(prepared.content, prepared.slotOffsets, prepared.writingOffsets).map((chunk, i) =>
          <div key={i}>{renderChunk(chunk.content, chunk.slotOffsets, chunk.writingOffsets)}</div>
        )}</div>
      </RichQuestionsContext.Provider>
      </RichActionContext.Provider>
    </WritingBlocksContext.Provider>;
  }
}
