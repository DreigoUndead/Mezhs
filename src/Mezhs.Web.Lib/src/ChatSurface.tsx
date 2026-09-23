import { FormEvent, KeyboardEvent, ReactNode, UIEvent, useEffect, useRef } from "react";
import type { ApiFile, ChatMessage } from "./providers/contracts";
import { MarkdownContent } from "./MarkdownContent";
import { useAutoResizeTextArea } from "./useAutoResizeTextArea";

export type ChatSurfaceMessage = Pick<
  ChatMessage,
  "messageId" | "connectionId" | "role" | "origin" | "content" | "status" | "createdAt" | "error" |
  "activity" | "activityDetail" | "analysis" | "activityAt"
> & {
  files?: ApiFile[];
};

export type ChatTranscriptProps = {
  messages: ChatSurfaceMessage[];
  apiBaseUrl?: string;
  busy?: boolean;
  busyLabel?: string;
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

export function modelActivityLabel(activity?: string | null, detail?: string | null) {
  const explicit = detail?.trim();
  if (explicit) return explicit;

  switch (activity?.trim().toLocaleLowerCase()) {
    case "submitting": return "Submitting prompt…";
    case "waiting": return "Waiting for model activity…";
    case "thinking": return "Model is thinking…";
    case "responding": return "Model is responding…";
    case "completed": return "Model response received.";
    case "active": return "Model activity observed…";
    case "retrying": return "Retrying model turn…";
    case "rate-limited": return "Model state check is rate limited…";
    default: return "Waiting for model…";
  }
}

export function ChatTranscript({
  messages,
  apiBaseUrl = "",
  busy = false,
  busyLabel,
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
  const activeMessage = [...messages].reverse().find((message) =>
    message.role === "user" && !terminalStatuses.has(message.status));
  const resolvedBusyLabel = busyLabel ??
    modelActivityLabel(activeMessage?.activity, activeMessage?.activityDetail);
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
            {message.content && <div className="message-content"><MarkdownContent content={message.content} /></div>}
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
            {message.analysis && (
              <details className="message-analysis">
                <summary>Model analysis</summary>
                <pre>{message.analysis}</pre>
              </details>
            )}
            {renderMessageFooter?.(message)}
            {onReplay && message.role === "user" && terminalStatuses.has(message.status) && (
              <button className="replay" onClick={() => onReplay(message.messageId)} disabled={replayDisabled}>Replay request</button>
            )}
          </div>
        </article>
      ))}
      {busy && messages.length > 0 && <div className="thinking"><i /><i /><i /><span>{resolvedBusyLabel}</span></div>}
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
  sideControls?: ReactNode;
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
  sideControls,
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
      <form className={sideControls ? "composer composer-with-side-controls" : "composer"} onSubmit={submit}>
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
        {sideControls && <div className="composer-side-controls">{sideControls}</div>}
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
