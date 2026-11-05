import { useMemo } from "react";
import Markdown, { uriTransformer } from "react-markdown";
import type { Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import remarkMdx from "remark-mdx";
import { remarkRichMarkup, richComponents } from "./RichMarkup";
import { expandRichTemplates } from "./RichExpression";
import { extractRichQuestions, RichQuestionSlot, RichQuestionsContext, RichQuestionData } from "./RichQuestion";
import { RichActionContext } from "./RichActionContext";
import "./markdown.css";

// Keep react-markdown protocol validation without emitting a javascript: placeholder.
const safeUri = (value: string) => {
  const validated = uriTransformer(value);
  return validated === "javascript:void(0)" ? "" : validated;
};

// One provider-neutral renderer. All model syntax is parsed as data, never
// compiled or evaluated. Only a real host callback grants send authority.
const components = { ...richComponents, "mezhs-question": RichQuestionSlot } as Components;

function renderRich(content: string, slotOffsets: Record<string, number>) {
  const parseMarkup = () => remarkRichMarkup(slotOffsets);
  return Markdown({
    children: content,
    remarkPlugins: [remarkGfm, remarkMdx as unknown as typeof remarkGfm, parseMarkup as unknown as typeof remarkGfm],
    components,
    transformLinkUri: safeUri,
    transformImageUri: safeUri,
  });
}

function renderChunk(content: string) {
  try {
    // Recovery chunks have lost their original Markdown context. Never rerun
    // action extraction here: a split fenced example could otherwise become
    // a live model-authored button. Static rich markup remains available.
    return renderRich(expandRichTemplates(content), Object.create(null));
  } catch {
    return <Markdown remarkPlugins={[remarkGfm]} transformLinkUri={safeUri} transformImageUri={safeUri}>{content}</Markdown>;
  }
}

export function MarkdownContent({ content, verbatim = false, onIssueNewTurn }: {
  content: string;
  verbatim?: boolean;
  onIssueNewTurn?: (content: string) => void;
}) {
  const prepared = useMemo(() => verbatim ? null : extractRichQuestions(expandRichTemplates(content)),
    [content, verbatim]);
  if (verbatim) return <div className="prompt-text">{content}</div>;
  try {
    if (!prepared) return null;
    return <RichActionContext.Provider value={onIssueNewTurn}>
      <RichQuestionsContext.Provider value={prepared.questions}>
        <div className="markdown-content">{renderRich(prepared.content, prepared.slotOffsets)}</div>
      </RichQuestionsContext.Provider>
    </RichActionContext.Provider>;
  } catch {
    // One malformed block must not disable unrelated rich blocks.
    // Fallback fragments are readable but never authorized to submit.
    return <RichActionContext.Provider value={undefined}>
      <div className="markdown-content">{content.split(/\n\s*\n/).map((chunk, i) =>
        <div key={i}>{renderChunk(chunk)}</div>
      )}</div>
    </RichActionContext.Provider>;
  }
}
