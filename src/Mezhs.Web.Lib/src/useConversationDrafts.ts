import { useState } from "react";

// Unsent text belongs to a chat (or a distinct new-chat context), not the active textbox.
export function useConversationDrafts(key: string) {
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const draft = drafts[key] ?? "";

  function setDraft(value: string) {
    setDrafts((current) => ({ ...current, [key]: value }));
  }

  function clearDraft(targetKey: string, expected: string) {
    setDrafts((current) => {
      if (current[targetKey] !== expected) return current;
      const next = { ...current };
      delete next[targetKey];
      return next;
    });
  }

  return { draft, setDraft, clearDraft };
}
