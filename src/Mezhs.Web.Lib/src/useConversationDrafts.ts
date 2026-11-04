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

  // Rename only the draft from the submitted context; keep edits made while awaiting the API.
  function moveDraft(sourceKey: string, targetKey: string, submitted: string) {
    if (sourceKey === targetKey) return;
    setDrafts((current) => {
      const remaining = current[sourceKey];
      if (remaining === undefined) return current;
      const next = { ...current };
      delete next[sourceKey];
      if (remaining !== submitted) {
        const existing = next[targetKey];
        next[targetKey] = existing && existing !== remaining
          ? existing + String.fromCharCode(10, 10) + remaining : remaining;
      }
      return next;
    });
  }

  // Roll back a failed request without discarding text entered afterward.
  function restoreDraft(targetKey: string, submitted: string) {
    setDrafts((current) => {
      const newer = current[targetKey];
      if (newer === submitted) return current;
      return { ...current, [targetKey]: newer ? submitted + String.fromCharCode(10, 10) + newer : submitted };
    });
  }

  return { draft, setDraft, clearDraft, moveDraft, restoreDraft };
}
