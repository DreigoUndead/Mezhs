import { useCallback, useRef, useSyncExternalStore } from "react";

// Draft edits belong to the composer, not the application that renders the history.
// This store is scoped to one app instance and notifies only the active draft's readers.
export function createConversationDraftStore() {
  const drafts = new Map<string, string>();
  const listeners = new Map<string, Set<() => void>>();

  function notify(key: string) {
    listeners.get(key)?.forEach((listener) => listener());
  }

  function getDraft(key: string) {
    return drafts.get(key) ?? "";
  }

  function setDraft(key: string, value: string) {
    if (getDraft(key) === value) return;
    drafts.set(key, value);
    notify(key);
  }

  function clearDraft(key: string, expected: string) {
    if (drafts.get(key) !== expected) return;
    drafts.delete(key);
    notify(key);
  }

  function moveDraft(sourceKey: string, targetKey: string, submitted: string) {
    if (sourceKey === targetKey) return;
    const remaining = drafts.get(sourceKey);
    if (remaining === undefined) return;
    drafts.delete(sourceKey);
    notify(sourceKey);
    if (remaining !== submitted) {
      const existing = drafts.get(targetKey);
      drafts.set(targetKey, existing && existing !== remaining
        ? existing + String.fromCharCode(10, 10) + remaining : remaining);
      notify(targetKey);
    }
  }

  function restoreDraft(key: string, submitted: string) {
    const newer = drafts.get(key);
    if (newer === submitted) return;
    drafts.set(key, newer ? submitted + String.fromCharCode(10, 10) + newer : submitted);
    notify(key);
  }

  function subscribe(key: string, listener: () => void) {
    let subscribers = listeners.get(key);
    if (!subscribers) {
      subscribers = new Set();
      listeners.set(key, subscribers);
    }
    subscribers.add(listener);
    return () => {
      subscribers.delete(listener);
      if (subscribers.size === 0) listeners.delete(key);
    };
  }

  return { getDraft, setDraft, clearDraft, moveDraft, restoreDraft, subscribe };
}

export type ConversationDraftStore = ReturnType<typeof createConversationDraftStore>;

export function useConversationDraftStore() {
  const store = useRef<ConversationDraftStore | null>(null);
  if (!store.current) store.current = createConversationDraftStore();
  return store.current;
}

export function useConversationDraftValue(store: ConversationDraftStore, key: string) {
  const subscribe = useCallback((listener: () => void) => store.subscribe(key, listener), [store, key]);
  const snapshot = useCallback(() => store.getDraft(key), [store, key]);
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

// Preserve the original hook for callers that intentionally own their draft state.
export function useConversationDrafts(key: string) {
  const store = useConversationDraftStore();
  const draft = useConversationDraftValue(store, key);
  return {
    draft,
    setDraft: (value: string) => store.setDraft(key, value),
    clearDraft: store.clearDraft,
    moveDraft: store.moveDraft,
    restoreDraft: store.restoreDraft,
  };
}
