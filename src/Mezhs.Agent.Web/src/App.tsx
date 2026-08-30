import { useEffect, useMemo, useState } from "react";
import {
  ChatComposer,
  ChatTranscript,
  expectJson,
  type ChatSurfaceMessage,
} from "@mezhs/web-lib";

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

type AgentChatMessage = {
  messageId: string;
  chatId: string;
  connectionId: string;
  role: "user" | "assistant";
  content: string;
  fileIds: string[];
  parentMessageId?: string;
  replayOfMessageId?: string;
  replyMessageId?: string;
  status: "Queued" | "Running" | "Completed" | "Failed" | "Cancelled";
  error?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
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

function toSharedMessage(message: AgentChatMessage): ChatSurfaceMessage {
  return {
    messageId: message.messageId,
    connectionId: message.connectionId,
    role: message.role,
    content: message.content,
    status: message.status,
    createdAt: message.createdAt,
    error: message.error,
  };
}

export default function App() {
  const [runtime, setRuntime] = useState<Runtime | null>(null);
  const [policies, setPolicies] = useState<AgentPolicy[]>([]);
  const [chats, setChats] = useState<AgentChat[]>([]);
  const [selectedChatId, setSelectedChatId] = useState<string | null>(null);
  const [messages, setMessages] = useState<AgentChatMessage[]>([]);
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
  const sharedMessages = useMemo(() => messages.map(toSharedMessage), [messages]);
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
        api<AgentChatMessage[]>(`/v1/agent-chats/${encodeURIComponent(chatId)}/messages`),
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

  async function submit() {
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
    for (let attempt = 0; attempt < 80; attempt++) {
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

  const composerDisabled = sending || !!activeExecution || !!selectedChat?.paused;
  const composerPlaceholder = selectedChat?.paused
    ? "Resume this agent chat to continue"
    : activeExecution
      ? "Agent execution is running"
      : creating
        ? "Describe the task for this agent"
        : "Continue this agent chat";

  return (
    <div className="agent-shell">
      <aside className="agent-sidebar">
        <div className="agent-brand-row">
          <div className="agent-brand-mark">M</div>
          <div><strong>MEŽS Agent</strong><span>Policy-controlled chats</span></div>
          <span className={`health-dot ${runtime?.mezhsApiHealthy ? "online" : ""}`}
            title={runtime?.mezhsApiHealthy ? "MEŽS API online" : "MEŽS API unavailable"} />
        </div>

        <button className="new-chat" type="button" onClick={beginNewChat}><span>+</span> New agent chat</button>

        <span className="section-label">Agent chats</span>
        <nav className="agent-chat-list" aria-label="Agent chats">
          {chats.map((chat) => (
            <button
              type="button"
              key={chat.chatId}
              className={`agent-chat-row ${selectedChatId === chat.chatId && !creating ? "selected" : ""}`}
              onClick={() => selectChat(chat.chatId)}
            >
              <span className="agent-chat-title">{displayTitle(chat)}</span>
              <small>{chat.policyId} · {chat.originSource}</small>
              {chat.paused && <i>paused</i>}
            </button>
          ))}
          {chats.length === 0 && <p className="agent-empty-sidebar">No agent chats yet.</p>}
        </nav>

        <div className="agent-sidebar-footer">
          <span className={`status-pill ${runtime?.mezhsApiHealthy ? "ready" : "offline"}`}>
            {runtime?.mezhsApiHealthy ? "API ready" : "API offline"}
          </span>
        </div>
      </aside>

      <main className="agent-main-panel">
        {creating || !selectedChat ? (
          <>
            <header className="agent-header">
              <div>
                <span className="eyebrow">Manual execution</span>
                <h1>New agent chat</h1>
              </div>
            </header>

            <section className="agent-new-chat">
              <p>Choose a policy for this chat. The policy fixes its rules, connection and executable capabilities.</p>
              <label className="agent-field-label" htmlFor="policy">Policy</label>
              <select
                id="policy"
                value={policyId}
                onChange={(event) => setPolicyId(event.target.value)}
                disabled={sending}
              >
                {policies.map((policy) => (
                  <option key={policy.id} value={policy.id}>{policy.id} · {policy.connectionId}</option>
                ))}
              </select>
              {selectedPolicy && (
                <div className="agent-policy-summary">
                  <strong>{selectedPolicy.id}</strong>
                  <span>Connection: {selectedPolicy.connectionId}</span>
                  {selectedPolicy.modelInstructions && <pre>{selectedPolicy.modelInstructions}</pre>}
                </div>
              )}
            </section>

            <ChatComposer
              value={draft}
              onChange={setDraft}
              onSubmit={submit}
              placeholder={composerPlaceholder}
              disabled={!policyId}
              busy={sending}
              notice={notice}
              onDismissNotice={() => setNotice(null)}
              disclaimer="Agent actions are governed by the selected policy and recorded in execution history."
            />
          </>
        ) : (
          <>
            <header className="agent-header">
              <div>
                <span className="eyebrow">{selectedChat.originSource} · {selectedChat.policyId}</span>
                <h1>{displayTitle(selectedChat)}</h1>
                <div className="agent-header-meta">
                  <span>{selectedChat.connectionId || selectedPolicy?.connectionId || "connection unavailable"}</span>
                  <span>·</span>
                  <span>{shortId(selectedChat.chatId)}</span>
                  {latestAgentExecution && (
                    <span className={`agent-execution-status ${statusTone(latestAgentExecution.status)}`}>
                      {latestAgentExecution.status}
                    </span>
                  )}
                </div>
              </div>
              <button
                type="button"
                className={selectedChat.paused ? "agent-primary" : "agent-secondary"}
                onClick={() => void togglePause()}
                disabled={togglingPause}
              >
                {togglingPause ? "Updating…" : selectedChat.paused ? "Resume" : "Pause"}
              </button>
            </header>

            {selectedChat.paused && <div className="agent-paused-banner">This agent chat is paused. New executions are blocked until it is resumed.</div>}

            <ChatTranscript
              messages={sharedMessages}
              busy={!!activeExecution}
              emptyState={<div className="agent-empty-chat">No conversation messages yet.</div>}
              getAuthorLabel={(message) => message.role === "assistant" ? "Agent" : "You"}
              getAvatarLabel={(message) => message.role === "assistant" ? "M" : "YOU"}
            />

            <ChatComposer
              value={draft}
              onChange={setDraft}
              onSubmit={submit}
              placeholder={composerPlaceholder}
              disabled={composerDisabled}
              busy={sending}
              notice={notice}
              onDismissNotice={() => setNotice(null)}
              disclaimer="Agent actions are governed by policy and recorded in execution history."
            />

            <details className="agent-execution-details">
              <summary>Execution history {shellExecutions.length > 0 && <span>{shellExecutions.length} shell</span>}</summary>
              <div className="agent-execution-list">
                {executions.map((execution) => (
                  <article className="agent-execution-row" key={execution.executionId}>
                    <div className="agent-execution-topline">
                      <strong>{execution.kind}</strong>
                      <span className={`agent-execution-status ${statusTone(execution.status)}`}>{execution.status}</span>
                      {execution.exitCode !== undefined && <span>exit {execution.exitCode}</span>}
                      <time>{formatTime(execution.createdAt)}</time>
                    </div>
                    <code>{execution.request}</code>
                    {execution.result && <pre>{execution.result}</pre>}
                    {execution.error && <pre className="agent-error-output">{execution.error}</pre>}
                  </article>
                ))}
                {executions.length === 0 && <p>No execution records yet.</p>}
              </div>
            </details>
          </>
        )}
      </main>
    </div>
  );
}
