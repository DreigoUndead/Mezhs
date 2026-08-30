import { FormEvent, useEffect, useMemo, useState } from "react";
import { expectJson } from "@mezhs/web-lib";
import type { ChatMessage } from "@mezhs/web-lib";

type Runtime = {
  status: string;
  mezhsApi: string;
  mezhsApiHealthy: boolean;
};

type AgentPolicy = {
  id: string;
  connectionId: string;
  modelInstructions: string;
  snapshot: string;
};

type AgentChat = {
  chatId: string;
  policyId: string;
  originSource: string;
  originReference?: string;
  paused: boolean;
  title?: string;
  connectionId?: string;
  createdAt: string;
  updatedAt: string;
};

type ExecutionStatus = "Queued" | "Running" | "Completed" | "Failed" | "Cancelled" | "Interrupted";
type ExecutionKind = "Agent" | "Shell";

type Execution = {
  executionId: string;
  parentExecutionId?: string;
  correlationId: string;
  kind: ExecutionKind;
  chatId?: string;
  policyId: string;
  connectionId: string;
  source: string;
  sourceReference?: string;
  status: ExecutionStatus;
  request: string;
  result?: string;
  error?: string;
  exitCode?: number;
  policySnapshot: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
};

const activeStatuses = new Set<ExecutionStatus>(["Queued", "Running"]);
const terminalStatuses = new Set<ExecutionStatus>(["Completed", "Failed", "Cancelled", "Interrupted"]);

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  return expectJson<T>(await fetch(path, init));
}

function formatTime(value?: string) {
  if (!value) return "";
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

function shortId(value: string) {
  return value.length <= 14 ? value : `${value.slice(0, 8)}…${value.slice(-5)}`;
}

function displayTitle(chat: AgentChat) {
  return chat.title?.trim() || `Agent chat ${shortId(chat.chatId)}`;
}

function statusTone(status: ExecutionStatus) {
  if (status === "Completed") return "good";
  if (status === "Failed") return "bad";
  if (status === "Cancelled" || status === "Interrupted") return "muted";
  return "active";
}

export default function App() {
  const [runtime, setRuntime] = useState<Runtime | null>(null);
  const [policies, setPolicies] = useState<AgentPolicy[]>([]);
  const [chats, setChats] = useState<AgentChat[]>([]);
  const [selectedChatId, setSelectedChatId] = useState<string | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [executions, setExecutions] = useState<Execution[]>([]);
  const [creating, setCreating] = useState(false);
  const [policyId, setPolicyId] = useState("");
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [togglingPause, setTogglingPause] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const selectedChat = chats.find((chat) => chat.chatId === selectedChatId) ?? null;
  const selectedPolicy = policies.find((policy) =>
    policy.id === (selectedChat?.policyId ?? policyId));
  const activeExecution = executions.find((execution) =>
    execution.kind === "Agent" && activeStatuses.has(execution.status));
  const latestAgentExecution = executions.find((execution) => execution.kind === "Agent");

  const shellExecutions = useMemo(
    () => executions.filter((execution) => execution.kind === "Shell"),
    [executions],
  );

  useEffect(() => {
    void (async () => {
      try {
        const [runtimeValue, policyValues, chatValues] = await Promise.all([
          api<Runtime>("/v1/runtime"),
          api<AgentPolicy[]>("/v1/policies"),
          api<AgentChat[]>("/v1/agent-chats"),
        ]);
        setRuntime(runtimeValue);
        setPolicies(policyValues);
        setChats(chatValues);
        setPolicyId(policyValues[0]?.id ?? "");
        if (chatValues.length > 0)
          setSelectedChatId(chatValues[0].chatId);
      } catch (error) {
        setNotice(error instanceof Error ? error.message : "Could not load MEŽS Agent.");
      }
    })();
  }, []);

  useEffect(() => {
    if (!selectedChatId || creating) {
      setMessages([]);
      setExecutions([]);
      return;
    }
    void loadSelected(selectedChatId);
  }, [selectedChatId, creating]);

  useEffect(() => {
    const timer = window.setInterval(() => {
      void refreshChats();
      if (selectedChatId && !creating)
        void loadSelected(selectedChatId, false);
    }, 1200);
    return () => window.clearInterval(timer);
  }, [selectedChatId, creating]);

  async function refreshChats() {
    try {
      setChats(await api<AgentChat[]>("/v1/agent-chats"));
    } catch {
      // Keep the last durable view during transient refresh failures.
    }
  }

  async function loadSelected(chatId: string, reportErrors = true) {
    try {
      const [messageValues, executionValues] = await Promise.all([
        api<ChatMessage[]>(`/v1/agent-chats/${encodeURIComponent(chatId)}/messages`),
        api<Execution[]>(`/v1/agent-chats/${encodeURIComponent(chatId)}/executions`),
      ]);
      setMessages(messageValues);
      setExecutions(executionValues);
    } catch (error) {
      if (reportErrors)
        setNotice(error instanceof Error ? error.message : "Could not load this agent chat.");
    }
  }

  function beginNewChat() {
    setCreating(true);
    setSelectedChatId(null);
    setMessages([]);
    setExecutions([]);
    setDraft("");
    setNotice(null);
    if (!policyId && policies.length > 0)
      setPolicyId(policies[0].id);
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    const input = draft.trim();
    const effectivePolicyId = selectedChat?.policyId ?? policyId;
    if (!input || !effectivePolicyId || sending || activeExecution || selectedChat?.paused)
      return;

    setSending(true);
    setNotice(null);
    try {
      const execution = await api<Execution>("/v1/executions", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          policyId: effectivePolicyId,
          input,
          ...(selectedChat ? { chatId: selectedChat.chatId } : {}),
        }),
      });
      setDraft("");

      if (selectedChat) {
        setExecutions((current) => [execution, ...current]);
        return;
      }

      const attachedChatId = await waitForAttachedChat(execution.executionId);
      setCreating(false);
      setSelectedChatId(attachedChatId);
      await refreshChats();
      await loadSelected(attachedChatId);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Agent execution could not be started.");
    } finally {
      setSending(false);
    }
  }

  async function waitForAttachedChat(executionId: string) {
    for (var attempt = 0; attempt < 80; attempt++) {
      const execution = await api<Execution>(`/v1/executions/${encodeURIComponent(executionId)}`);
      if (execution.chatId)
        return execution.chatId;
      if (terminalStatuses.has(execution.status))
        throw new Error(execution.error || `Execution ended as ${execution.status} before a chat was attached.`);
      await new Promise((resolve) => window.setTimeout(resolve, 250));
    }
    throw new Error("The agent execution did not attach a chat in time.");
  }

  async function togglePause() {
    if (!selectedChat || togglingPause)
      return;
    setTogglingPause(true);
    setNotice(null);
    try {
      const updated = await api<AgentChat>(
        `/v1/agent-chats/${encodeURIComponent(selectedChat.chatId)}`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ paused: !selectedChat.paused }),
        },
      );
      setChats((current) => current.map((chat) =>
        chat.chatId === updated.chatId ? updated : chat));
      await loadSelected(updated.chatId);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Agent chat state could not be changed.");
    } finally {
      setTogglingPause(false);
    }
  }

  function selectChat(chatId: string) {
    setCreating(false);
    setSelectedChatId(chatId);
    setNotice(null);
  }

  return (
    <div className="agent-shell">
      <aside className="agent-sidebar">
        <div className="brand-row">
          <div>
            <div className="eyebrow">MEŽS</div>
            <h1>Agent</h1>
          </div>
          <span className={`health-dot ${runtime?.mezhsApiHealthy ? "online" : "offline"}`}
            title={runtime?.mezhsApiHealthy ? "MEŽS API online" : "MEŽS API unavailable"} />
        </div>

        <button className="primary wide" type="button" onClick={beginNewChat}>
          + New agent chat
        </button>

        <div className="sidebar-label">Agent chats</div>
        <nav className="chat-list" aria-label="Agent chats">
          {chats.map((chat) => (
            <button
              type="button"
              key={chat.chatId}
              className={`chat-row ${selectedChatId === chat.chatId && !creating ? "selected" : ""}`}
              onClick={() => selectChat(chat.chatId)}
            >
              <span className="chat-title">{displayTitle(chat)}</span>
              <span className="chat-meta">
                <span>{chat.policyId}</span>
                <span>·</span>
                <span>{chat.originSource}</span>
                {chat.paused && <span className="paused-mini">paused</span>}
              </span>
            </button>
          ))}
          {chats.length === 0 && <div className="empty-sidebar">No agent chats yet.</div>}
        </nav>
      </aside>

      <main className="agent-main">
        {notice && <div className="notice">{notice}</div>}

        {creating || !selectedChat ? (
          <section className="new-chat-panel">
            <div className="section-kicker">Manual execution</div>
            <h2>New agent chat</h2>
            <p>
              Choose any configured policy. The web UI is the source of this execution; the policy still owns
              the chat rules, connection and executable capabilities.
            </p>

            <label className="field-label" htmlFor="policy">Policy</label>
            <select
              id="policy"
              value={policyId}
              onChange={(event) => setPolicyId(event.target.value)}
              disabled={sending}
            >
              {policies.map((policy) => (
                <option key={policy.id} value={policy.id}>
                  {policy.id} · {policy.connectionId}
                </option>
              ))}
            </select>

            {selectedPolicy && (
              <div className="policy-summary">
                <strong>{selectedPolicy.id}</strong>
                <span>Connection: {selectedPolicy.connectionId}</span>
                {selectedPolicy.modelInstructions && <p>{selectedPolicy.modelInstructions}</p>}
              </div>
            )}

            <Composer
              draft={draft}
              setDraft={setDraft}
              disabled={sending || !policyId}
              label={sending ? "Starting…" : "Start agent chat"}
              onSubmit={submit}
            />
          </section>
        ) : (
          <>
            <header className="chat-header">
              <div className="chat-heading">
                <div className="section-kicker">{selectedChat.originSource} · {selectedChat.policyId}</div>
                <h2>{displayTitle(selectedChat)}</h2>
                <div className="header-meta">
                  <span>{selectedChat.connectionId || selectedPolicy?.connectionId || "connection unavailable"}</span>
                  <span>·</span>
                  <span>{shortId(selectedChat.chatId)}</span>
                  {latestAgentExecution && (
                    <span className={`status-pill ${statusTone(latestAgentExecution.status)}`}>
                      {latestAgentExecution.status}
                    </span>
                  )}
                </div>
              </div>
              <button
                type="button"
                className={selectedChat.paused ? "primary" : "secondary"}
                onClick={() => void togglePause()}
                disabled={togglingPause}
                title={selectedChat.paused
                  ? "Allow new executions again"
                  : "Pause this chat and cancel queued/running execution"}
              >
                {togglingPause ? "Updating…" : selectedChat.paused ? "Resume" : "Pause"}
              </button>
            </header>

            {selectedChat.paused && (
              <div className="paused-banner">
                This agent chat is paused. New executions are blocked until it is resumed.
              </div>
            )}

            <section className="transcript" aria-label="Conversation">
              {messages.map((message) => (
                <article key={message.messageId} className={`message ${message.role}`}>
                  <div className="message-role">{message.role === "assistant" ? "Agent" : "MEŽS"}</div>
                  <pre>{message.content}</pre>
                  {message.files.length > 0 && (
                    <div className="file-row">
                      {message.files.map((file) => <span key={file.fileId}>{file.name}</span>)}
                    </div>
                  )}
                  {message.error && <div className="message-error">{message.error}</div>}
                  <time>{formatTime(message.createdAt)}</time>
                </article>
              ))}
              {messages.length === 0 && <div className="empty-transcript">No conversation messages yet.</div>}
            </section>

            <div className="composer-wrap">
              <Composer
                draft={draft}
                setDraft={setDraft}
                disabled={sending || !!activeExecution || selectedChat.paused}
                label={selectedChat.paused
                  ? "Paused"
                  : activeExecution
                    ? `${activeExecution.status}…`
                    : sending
                      ? "Starting…"
                      : "Run agent"}
                onSubmit={submit}
              />
            </div>

            <details className="execution-details">
              <summary>
                Execution history
                {shellExecutions.length > 0 && <span>{shellExecutions.length} shell</span>}
              </summary>
              <div className="execution-list">
                {executions.map((execution) => (
                  <div className="execution-row" key={execution.executionId}>
                    <div className="execution-topline">
                      <strong>{execution.kind}</strong>
                      <span className={`status-pill ${statusTone(execution.status)}`}>{execution.status}</span>
                      {execution.exitCode !== undefined && <span>exit {execution.exitCode}</span>}
                      <time>{formatTime(execution.createdAt)}</time>
                    </div>
                    <code>{execution.request}</code>
                    {execution.result && <pre>{execution.result}</pre>}
                    {execution.error && <div className="message-error">{execution.error}</div>}
                  </div>
                ))}
                {executions.length === 0 && <div className="empty-transcript">No executions recorded.</div>}
              </div>
            </details>
          </>
        )}
      </main>
    </div>
  );
}

type ComposerProps = {
  draft: string;
  setDraft: (value: string) => void;
  disabled: boolean;
  label: string;
  onSubmit: (event: FormEvent) => void;
};

function Composer({ draft, setDraft, disabled, label, onSubmit }: ComposerProps) {
  return (
    <form className="composer" onSubmit={onSubmit}>
      <textarea
        value={draft}
        onChange={(event) => setDraft(event.target.value)}
        placeholder="Tell the agent what to do…"
        rows={4}
        disabled={disabled}
        onKeyDown={(event) => {
          if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            event.currentTarget.form?.requestSubmit();
          }
        }}
      />
      <button className="primary" type="submit" disabled={disabled || !draft.trim()}>
        {label}
      </button>
    </form>
  );
}
