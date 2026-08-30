import { FormEvent, KeyboardEvent, ReactNode } from "react";
import type { ApiFile, ChatMessage } from "./providers/contracts";

export type ChatSurfaceMessage = Pick<
  ChatMessage,
  "messageId" | "connectionId" | "role" | "content" | "status" | "createdAt" | "error"
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
  onReplay?: (messageId: string) => void;
  replayDisabled?: boolean;
};

const terminalStatuses = new Set(["Completed", "Failed", "Cancelled"]);

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
  onReplay,
  replayDisabled = false,
}: ChatTranscriptProps) {
  const apiBase = apiBaseUrl.replace(/\/$/, "");

  return (
    <section className={`conversation ${messages.length === 0 ? "conversation-empty" : ""}`} aria-live="polite">
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
