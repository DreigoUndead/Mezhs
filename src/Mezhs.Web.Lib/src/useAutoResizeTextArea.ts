import { RefObject, useLayoutEffect } from "react";

export function useAutoResizeTextArea(ref: RefObject<HTMLTextAreaElement>, value: string) {
  useLayoutEffect(() => {
    const textarea = ref.current;
    if (!textarea) return;

    textarea.style.height = "0px";
    const nextHeight = Math.min(textarea.scrollHeight, 160);
    textarea.style.height = `${nextHeight}px`;
    textarea.style.overflowY = textarea.scrollHeight > nextHeight ? "auto" : "hidden";
  }, [ref, value]);
}
