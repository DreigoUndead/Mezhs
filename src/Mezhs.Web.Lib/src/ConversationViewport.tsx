import { ReactNode, UIEvent, useEffect, useLayoutEffect, useRef, useState } from "react";
import { ArrowDown, ArrowUp } from "lucide-react";

type MessageVersion = { messageId: string };

type ScrollSnapshot = {
  top: number;
  revision: string;
  followingBottom: boolean;
};

type ConversationViewportProps = {
  conversationKey: string;
  messages: MessageVersion[];
  revisionItems?: readonly unknown[];
  busy?: boolean;
  empty?: boolean;
  enabled?: boolean;
  historyReady?: boolean;
  children: ReactNode;
};

const threshold = 96;

// Include all serializable message fields and optional agent-execution footers.
// Keep only a compact fingerprint in per-chat scroll snapshots.
function contentRevision(messages: MessageVersion[], revisionItems: readonly unknown[] | undefined, busy: boolean) {
  const serialized = JSON.stringify([messages, revisionItems ?? null, busy]);
  let first = 2166136261;
  let second = 2654435761;
  for (let index = 0; index < serialized.length; index++) {
    const code = serialized.charCodeAt(index);
    first = Math.imul(first ^ code, 16777619);
    second = Math.imul(second ^ code, 2246822519);
  }
  return serialized.length + ":" + (first >>> 0) + ":" + (second >>> 0);
}

export function ConversationViewport({
  conversationKey, messages, revisionItems, busy = false, empty = false, enabled = true, historyReady = true, children,
}: ConversationViewportProps) {
  const scrollRef = useRef<HTMLElement>(null);
  const contentRef = useRef<HTMLDivElement>(null);
  const revisionCache = useRef<{ messages: MessageVersion[]; revisionItems?: readonly unknown[]; busy: boolean; value: string } | null>(null);
  const positions = useRef(new Map<string, ScrollSnapshot>());
  const activeKey = useRef(conversationKey);
  const pendingRestore = useRef(true);
  const followingBottom = useRef(true);
  const lastScrollTop = useRef(0);
  const [navigation, setNavigation] = useState({ top: false, bottom: false });

  function revision() {
    const cached = revisionCache.current;
    if (cached && cached.messages === messages && cached.revisionItems === revisionItems && cached.busy === busy)
      return cached.value;
    const value = contentRevision(messages, revisionItems, busy);
    revisionCache.current = { messages, revisionItems, busy, value };
    return value;
  }

  function updateNavigation(element: HTMLElement) {
    const remaining = Math.max(0, element.scrollHeight - element.clientHeight);
    const next = {
      top: element.scrollTop > threshold,
      bottom: remaining - element.scrollTop > threshold,
    };
    setNavigation((current) =>
      current.top === next.top && current.bottom === next.bottom ? current : next);
  }

  useLayoutEffect(() => {
    if (activeKey.current !== conversationKey) {
      activeKey.current = conversationKey;
      pendingRestore.current = true;
      followingBottom.current = true;
      setNavigation({ top: false, bottom: false });
    }
  }, [conversationKey]);

  useLayoutEffect(() => {
    if (!enabled) return;
    const element = scrollRef.current;
    if (!element) return;
    // Wait for both message history and related execution records.
    if (pendingRestore.current && !historyReady) return;

    if (pendingRestore.current) {
      const previous = positions.current.get(conversationKey);
      const restore = !!previous && !previous.followingBottom && previous.revision === revision();
      element.scrollTop = restore ? previous.top : element.scrollHeight;
      lastScrollTop.current = element.scrollTop;
      followingBottom.current = !restore;
      pendingRestore.current = false;
    } else if (followingBottom.current) {
      element.scrollTop = element.scrollHeight;
      lastScrollTop.current = element.scrollTop;
    }
    updateNavigation(element);
  }, [conversationKey, messages, revisionItems, busy, enabled, historyReady]);

  // Observe the viewport and its content for resizing and asynchronous growth.
  useEffect(() => {
    if (!enabled || typeof ResizeObserver === "undefined") return;
    const element = scrollRef.current;
    const content = contentRef.current;
    if (!element || !content) return;
    const observer = new ResizeObserver(() => {
      if (pendingRestore.current) return;
      if (followingBottom.current) {
        element.scrollTop = element.scrollHeight;
        lastScrollTop.current = element.scrollTop;
      }
      updateNavigation(element);
    });
    observer.observe(element);
    observer.observe(content);
    return () => observer.disconnect();
  }, [conversationKey, enabled]);

  function onScroll(event: UIEvent<HTMLElement>) {
    if (!enabled || pendingRestore.current) return;
    const element = event.currentTarget;
    if (Math.abs(element.scrollTop - lastScrollTop.current) < 1) return;
    lastScrollTop.current = element.scrollTop;
    const remaining = element.scrollHeight - element.scrollTop - element.clientHeight;
    followingBottom.current = remaining <= threshold;
    positions.current.set(conversationKey, {
      top: element.scrollTop, revision: revision(), followingBottom: followingBottom.current,
    });
    updateNavigation(element);
  }

  function jump(toBottom: boolean) {
    const element = scrollRef.current;
    if (!element) return;
    followingBottom.current = toBottom;
    positions.current.set(conversationKey, {
      top: toBottom ? element.scrollHeight : 0,
      revision: revision(),
      followingBottom: toBottom,
    });
    element.scrollTo({ top: toBottom ? element.scrollHeight : 0, behavior: "auto" });
    lastScrollTop.current = element.scrollTop;
    updateNavigation(element);
  }

  return (
    <div className="conversation-viewport">
      <section
        ref={scrollRef}
        className={`conversation ${empty ? "conversation-empty" : ""}`}
        aria-live="polite"
        onScroll={onScroll}
      >
        <div ref={contentRef} className="conversation-content">{children}</div>
      </section>
      {enabled && (navigation.top || navigation.bottom) && (
        <nav className="conversation-navigation" aria-label="Conversation navigation">
          {navigation.top && (
            <button type="button" title="Scroll to top" aria-label="Scroll to top" onClick={() => jump(false)}>
              <ArrowUp size={17} aria-hidden="true" />
            </button>
          )}
          {navigation.bottom && (
            <button type="button" title="Scroll to bottom" aria-label="Scroll to bottom" onClick={() => jump(true)}>
              <ArrowDown size={17} aria-hidden="true" />
            </button>
          )}
        </nav>
      )}
    </div>
  );
}
