import { ReactNode, UIEvent, useLayoutEffect, useRef, useState } from "react";
import { ArrowDown, ArrowUp } from "lucide-react";

type MessageVersion = {
  messageId: string;
  status: string;
  content: string;
  activityAt?: string | null;
};

type ScrollSnapshot = {
  top: number;
  version: string;
  followingBottom: boolean;
};

type ConversationViewportProps = {
  conversationKey: string;
  messages: MessageVersion[];
  busy?: boolean;
  empty?: boolean;
  enabled?: boolean;
  children: ReactNode;
};

const threshold = 96;

export function ConversationViewport({
  conversationKey, messages, busy = false, empty = false, enabled = true, children,
}: ConversationViewportProps) {
  const scrollRef = useRef<HTMLElement>(null);
  const positions = useRef(new Map<string, ScrollSnapshot>());
  const activeKey = useRef(conversationKey);
  const pendingRestore = useRef(true);
  const followingBottom = useRef(true);
  const [navigation, setNavigation] = useState({ top: false, bottom: false });
  // A changed message invalidates a manually saved position for that chat.
  const version = messages.map((message) =>
    `${message.messageId}:${message.status}:${message.content.length}:${message.activityAt ?? ""}`).join("|");

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
    // A chat is initially empty while its history loads. Restore only after loading.
    if (pendingRestore.current && messages.length === 0) return;

    if (pendingRestore.current) {
      const previous = positions.current.get(conversationKey);
      const restore = previous?.version === version && !previous.followingBottom;
      element.scrollTop = restore ? previous.top : element.scrollHeight;
      followingBottom.current = !restore;
      pendingRestore.current = false;
    } else if (followingBottom.current) {
      element.scrollTop = element.scrollHeight;
    }
    updateNavigation(element);
  }, [conversationKey, version, busy, children, enabled]);

  function onScroll(event: UIEvent<HTMLElement>) {
    if (!enabled || pendingRestore.current) return;
    const element = event.currentTarget;
    const remaining = element.scrollHeight - element.scrollTop - element.clientHeight;
    followingBottom.current = remaining <= threshold;
    positions.current.set(conversationKey, {
      top: element.scrollTop, version, followingBottom: followingBottom.current,
    });
    updateNavigation(element);
  }

  function jump(toBottom: boolean) {
    const element = scrollRef.current;
    if (!element) return;
    followingBottom.current = toBottom;
    positions.current.set(conversationKey, {
      top: toBottom ? element.scrollHeight : 0,
      version,
      followingBottom: toBottom,
    });
    element.scrollTo({ top: toBottom ? element.scrollHeight : 0, behavior: "smooth" });
  }

  return (
    <div className="conversation-viewport">
      <section
        ref={scrollRef}
        className={`conversation ${empty ? "conversation-empty" : ""}`}
        aria-live="polite"
        onScroll={onScroll}
      >
        {children}
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
