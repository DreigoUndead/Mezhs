import { createElement, Fragment, useContext, useId, useState } from "react";
import type { ReactNode } from "react";
import { RichActionContext } from "../RichActionContext";
import type { RichNode } from "../RichDocumentView";
import { boxComponent } from "./Box";

export function CanonicalQuestion({ node, renderChild }: { node: RichNode; renderChild: (child: RichNode) => ReactNode }) {
  const onIssueNewTurn = useContext(RichActionContext);
  const groupName = useId();
  const options = (node.children || []).filter(child => child.kind === "questionOption");
  const button = node.children?.find(child => child.kind === "questionButton");
  const [choice, setChoice] = useState(node.attributes?.initial || "");
  const selected = options.find(option => option.attributes?.value === choice);
  const message = selected?.attributes?.message;
  const enabled = !!onIssueNewTurn && typeof message === "string" && message.length > 0;
  if (!button || options.length < 2 || options.length > 12 ||
      !options.some(option => option.attributes?.value === node.attributes?.initial)) return null;
  const label = (children?: RichNode[]) => children?.map(child =>
    <Fragment key={child.id}>{renderChild(child)}</Fragment>);
  const control = <fieldset className="rich-question">
    <legend className="rich-question-caption">Choose one option</legend>
    {options.map(option => <label key={option.id} className="rich-question-option">
      <input type="radio" name={groupName} value={option.attributes?.value || ""}
        checked={choice === option.attributes?.value}
        onChange={() => setChoice(option.attributes?.value || "")} />
      <div>{label(option.children || [])}</div>
    </label>)}
    <button type="button" className={button.attributes?.block === "true" ? "rich-question-block" : undefined}
      disabled={!enabled} onClick={() => { if (enabled && message) onIssueNewTurn?.(message); }}>
      {label(button.children || [])}
    </button>
    {!onIssueNewTurn && <small className="rich-question-readonly">Submission unavailable in this view.</small>}
  </fieldset>;
  if (node.attributes?.box !== "true") return control;
  const props: Record<string, string> = {};
  for (const [name, value] of Object.entries(node.attributes || {}))
    if (name.startsWith("box."))
      props["data-mezhs-" + name.slice(4)] = value;
  return createElement(boxComponent, props, control);
}
