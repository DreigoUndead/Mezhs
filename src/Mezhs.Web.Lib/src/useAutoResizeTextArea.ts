import { RefObject, useEffect } from "react";

export function useAutoResizeTextArea(ref: RefObject<HTMLTextAreaElement>, value: string) {
  useEffect(() => {
    const textarea = ref.current;
    if (!textarea) return;

    textarea.style.height = "auto";
    textarea.style.height = `${textarea.scrollHeight}px`;
  }, [ref, value]);
}
