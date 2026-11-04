import { useRef, useState } from 'react';

// Pending requests are owned by their conversation, not the currently selected UI.
export function useConversationPending(key: string) {
  const pendingKeys = useRef(new Set<string>());
  const [, refresh] = useState(0);
  const notify = () => refresh((value) => value + 1);

  function start(targetKey: string) {
    if (pendingKeys.current.has(targetKey)) return false;
    pendingKeys.current.add(targetKey);
    notify();
    return true;
  }

  function transfer(sourceKey: string, targetKey: string) {
    if (sourceKey === targetKey || !pendingKeys.current.delete(sourceKey)) return;
    pendingKeys.current.add(targetKey);
    notify();
  }

  function finish(targetKey: string) {
    if (pendingKeys.current.delete(targetKey)) notify();
  }

  return { pending: pendingKeys.current.has(key), start, transfer, finish };
}
