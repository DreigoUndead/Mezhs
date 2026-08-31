import { FormEvent, KeyboardEvent, ReactNode, UIEvent, useEffect, useRef } from "react";
import type { ApiFile, ChatMessage } from "./providers/contracts";
import { useAutoResizeTextArea } from "./useAutoResizeTextArea";

export type ChatSurfaceMessage = Pick<
  ChatMessage,
  "messageId" | "connectionId" | "role" | "origin" | "content" | "status" | "createdAt" | "error"
> & {
  files?: ApiFile[];
};

export type ChatTranscriptProps = {
  messages: ChatSurfaceMessage[];
  apiBaseUrl?: string;
  busy?: boolean;
  emptyState?: ReactNode;
  getAuthorLabel?: (message: ChatSurfaceMessage) => string;
  getAvatarLabel?: (message: ChatSurfaceMessage) => string;
  renderMessageFooter?: (message: ChatSurfaceMessage) => ReactNode;
  onReplay?: (messageId: string) => void;
  replayDisabled?: boolean;
  autoScroll?: boolean;
  autoScrollResetKey?: string;
};

const terminalStatuses = new Set(["Completed", "Failed", "Cancelled"]);
const autoScrollThreshold = 96;

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

function defaultAuthorLabel(message: ChatSurfaceMessage) {
  return message.role === "assistant" ? "Assistant" : "You";
}

function defaultAvatarLabel(message: ChatSurfaceMessage) {
  return message.role === "assistant" ? "M" : "YOU";
}

export function ChatTranscript({
  messages,
  apiBaseUrl = "",
  busy = false,
  emptyState,
  getAuthorLabel = defaultAuthorLabel,
  getAvatarLabel = defaultAvatarLabel,
  renderMessageFooter,
  onReplay,
  replayDisabled = false,
  autoScroll = false,
  autoScrollResetKey,
}: ChatTranscriptProps) {
  const apiBase = apiBaseUrl.replace(/\/$/, "");
  const transcriptRef = useRef<HTMLElement | null>(null);
  const followsBottomRef = useRef(true);

  useEffect(() => {
    if (!autoScroll) return;
    followsBottomRef.current = true;
    const frame = window.requestAnimationFrame(() => {
      const transcript = transcriptRef.current;
      if (transcript) transcript.scrollTop = transcript.scrollHeight;
    });
    return () => window.cancelAnimationFrame(frame);
  }, [autoScroll, autoScrollResetKey]);

  useEffect(() => {
    if (!autoScroll || !followsBottomRef.current) return;
    const frame = window.requestAnimationFrame(() => {
      const transcript = transcriptRef.current;
      if (!transcript) return;
      transcript.scrollTo({ top: transcript.scrollHeight, behavior: "smooth" });
    });
    return () => window.cancelAnimationFrame(frame);
  }, [autoScroll, messages, busy]);

  function handleScroll(event: UIEvent<HTMLElement>) {
    if (!autoScroll) return;
    const transcript = event.currentTarget;
    const distanceFromBottom = transcript.scrollHeight - transcript.scrollTop - transcript.clientHeight;
    followsBottomRef.current = distanceFromBottom <= autoScrollThreshold;
  }

  return (
    <section
      ref={transcriptRef}
      className={`conversation ${messages.length === 0 ? "conversation-empty" : ""}`}
      aria-live="polite"
      onScroll={handleScroll}
    >
      {messages.length === 0 ? (emptyState ?? <div className="shared-chat-empty">No messages yet.</div>) : messages.map((message) => (
        <article key={message.messageId} className={`message ${message.role}`}>
          <div className="message-avatar">{getAvatarLabel(message)}</div>
          <div className="message-body">
            <div className="message-meta">
              <strong>{getAuthorLabel(message)}</strong>
              <span>{new Date(message.createdAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span>
              {message.status !== "Completed" && (
                <span className={`message-status ${message.status.toLowerCase()}`}>{message.status}</span>
              )}
            </div>
            {message.content && <div className="message-content">{message.content}</div>}
            {(message.files?.length ?? 0) > 0 && (
              <div className="message-files">
                {message.files!.map((file) => file.contentType.startsWith("image/") ? (
                  <a className="message-image" key={file.fileId} href={`${apiBase}${file.contentUrl}`} target="_blank" rel="noreferrer">
                    <img src={`${apiBase}${file.contentUrl}`} alt={file.name} />
                    <span>{file.name}<small>{formatBytes(file.size)}</small></span>
                  </a>
                ) : (
                  <a className="message-file" key={file.fileId} href={`${apiBase}${file.downloadUrl}`}>
                    <b>FILE</b><span>{file.name}<small>{formatBytes(file.size)}</small></span><i>Download</i>
                  </a>
                ))}
              </div>
            )}
            {message.error && <p className="message-error">{message.error}</p>}
            {renderMessageFooter?.(message)}
            {onReplay && message.role === "user" && terminalStatuses.has(message.status) && (
              <button className="replay" onClick={() => onReplay(message.messageId)} disabled={replayDisabled}>Replay request</button>
            )}
          </div>
        </article>
      ))}
      {busy && messages.length > 0 && <div className="thinking"><i /><i /><i /><span>Working through it...</span></div>}
    </section>
  );
}

export type ChatComposerProps = {
  value: string;
  onChange: (value: string) => void;
  onSubmit: () => void | Promise<void>;
  placeholder: string;
  disabled?: boolean;
  busy?: boolean;
  notice?: string | null;
  onDismissNotice?: () => void;
  hint?: string;
  disclaimer?: string;
  leadingActions?: ReactNode;
};

export function ChatComposer({
  value,
  onChange,
  onSubmit,
  placeholder,
  disabled = false,
  busy = false,
  notice,
  onDismissNotice,
  hint = "Enter to send / Shift + Enter for a new line",
  disclaimer,
  leadingActions,
}: ChatComposerProps) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  useAutoResizeTextArea(textareaRef, value);

  function submit(event?: FormEvent) {
    event?.preventDefault();
    if (disabled || busy || !value.trim()) return;
    void onSubmit();
  }

  function handleKey(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      submit();
    }
  }

  return (
    <footer className="composer-wrap">
      {notice && (
        <div className="notice">
          <span>{notice}</span>
          {onDismissNotice && <button type="button" onClick={onDismissNotice} aria-label="Dismiss">x</button>}
        </div>
      )}
      <form className="composer" onSubmit={submit}>
        <textarea
          ref={textareaRef}
          value={value}
          onChange={(event) => onChange(event.target.value)}
          onKeyDown={handleKey}
          placeholder={placeholder}
          rows={1}
          disabled={disabled || busy}
          aria-label="Message"
        />
        <div className="composer-actions">
          {leadingActions}
          <span>{hint}</span>
          <button
            className="send"
            type="submit"
            disabled={disabled || busy || !value.trim()}
            aria-label="Send message"
          >^</button>
        </div>
      </form>
      {disclaimer && <small className="disclaimer">{disclaimer}</small>}
    </footer>
  );
}
