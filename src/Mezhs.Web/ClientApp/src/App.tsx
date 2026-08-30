import { FormEvent, KeyboardEvent, useEffect, useMemo, useRef, useState } from "react";
import { ChatProviderRegistry } from "./providers/registry";
import type {
  ApiFile,
  Chat,
  ChatMessage as Message,
  Connection,
  ConnectionModel,
} from "./providers/contracts";

type Category = {
  categoryId: string;
  name: string;
  color: string;
  createdAt: string;
};

type PendingFile = {
  key: string;
  file: File;
  previewUrl?: string;
  uploading: boolean;
  uploaded?: ApiFile;
  error?: string;
};

const terminalStatuses = new Set(["Completed", "Failed", "Cancelled"]);

function makeInitials(name: string) {
  return name.split(/\s+/).map((word) => word[0]).join("").slice(0, 2).toUpperCase();
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

async function expectJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => ({ error: response.statusText }));
    throw new Error(body.error || `Request failed (${response.status})`);
  }
  return response.json() as Promise<T>;
}

export default function App() {
  const [apiBase, setApiBase] = useState("");
  const [connections, setConnections] = useState<Connection[]>([]);
  const [connectionId, setConnectionId] = useState("");
  const [models, setModels] = useState<ConnectionModel[]>([]);
  const [modelId, setModelId] = useState("");
  const [modelsLoading, setModelsLoading] = useState(false);
  const [categories, setCategories] = useState<Category[]>([]);
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [newChatCategoryId, setNewChatCategoryId] = useState("");
  const [newCategoryName, setNewCategoryName] = useState("");
  const [showCategoryForm, setShowCategoryForm] = useState(false);
  const [search, setSearch] = useState("");
  const [chats, setChats] = useState<Chat[]>([]);
  const [chatId, setChatId] = useState<string | null>(null);
  const [messages, setMessages] = useState<Message[]>([]);
  const [draft, setDraft] = useState("");
  const [pendingFiles, setPendingFiles] = useState<PendingFile[]>([]);
  const [online, setOnline] = useState(false);
  const [loading, setLoading] = useState(true);
  const [sending, setSending] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const providerRegistry = useRef(new ChatProviderRegistry());

  const selectedConnection = useMemo(
    () => connections.find((connection) => connection.id === connectionId),
    [connections, connectionId],
  );
  const selectedModel = useMemo(
    () => models.find((model) => (model.id || "") === modelId),
    [models, modelId],
  );
  const selectedProvider = providerRegistry.current.tryGet(connectionId);
  const activeChat = chats.find((chat) => chat.chatId === chatId);

  const filteredChats = useMemo(() => {
    const query = search.trim().toLocaleLowerCase();
    return chats.filter((chat) => {
      if (categoryFilter === "uncategorized" && chat.categoryId) return false;
      if (categoryFilter !== "all" && categoryFilter !== "uncategorized" && chat.categoryId !== categoryFilter) return false;
      if (!query) return true;
      const connection = connections.find((item) => item.id === chat.connectionId);
      const category = categories.find((item) => item.categoryId === chat.categoryId);
      return [chat.title, connection?.name, category?.name]
        .some((value) => value?.toLocaleLowerCase().includes(query));
    });
  }, [chats, connections, categories, categoryFilter, search]);

  useEffect(() => {
    void (async () => {
      try {
        const config = await expectJson<{ apiBaseUrl: string }>(await fetch("/app-config"));
        const base = config.apiBaseUrl.replace(/\/$/, "");
        setApiBase(base);
        const [healthResponse, connectionResponse, chatResponse, categoryResponse] = await Promise.all([
          fetch(`${base}/health`),
          fetch(`${base}/v1/connections`),
          fetch(`${base}/v1/chats`),
          fetch(`${base}/v1/categories`),
        ]);
        setOnline(healthResponse.ok);
        const available = await expectJson<Connection[]>(connectionResponse);
        providerRegistry.current.configure(base, available);
        setConnections(available);
        setChats(await expectJson<Chat[]>(chatResponse));
        setCategories(await expectJson<Category[]>(categoryResponse));
        if (available.length) setConnectionId(available[0].id);
      } catch (error) {
        setNotice(error instanceof Error ? error.message : "Could not reach MEŽS.");
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  useEffect(() => () => providerRegistry.current.dispose(), []);

  useEffect(() => {
    let cancelled = false;
    setModels([]);
    setModelId("");
    if (!selectedConnection?.supportsModels) {
      setModelsLoading(false);
      return () => { cancelled = true; };
    }

    setModelsLoading(true);
    void providerRegistry.current.get(selectedConnection.id).getModels()
      .then((available) => {
        if (cancelled) return;
        setModels(available);
        const configured = selectedConnection.defaultModel || "";
        setModelId(available.some((model) => (model.id || "") === configured) ? configured : "");
      })
      .catch((error) => {
        if (!cancelled)
          setNotice(error instanceof Error ? error.message : "Could not load models.");
      })
      .finally(() => {
        if (!cancelled) setModelsLoading(false);
      });

    return () => { cancelled = true; };
  }, [selectedConnection]);

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, sending]);

  async function loadChats(base = apiBase) {
    try {
      setChats(await expectJson<Chat[]>(await fetch(`${base}/v1/chats`)));
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Could not load conversations.");
    }
  }

  async function loadCategories(base = apiBase) {
    setCategories(await expectJson<Category[]>(await fetch(`${base}/v1/categories`)));
  }

  async function openChat(chat: Chat) {
    clearPendingFiles();
    setChatId(chat.chatId);
    setConnectionId(chat.connectionId);
    setSidebarOpen(false);
    try {
      const loaded = await providerRegistry.current.get(chat.connectionId).getChat(chat.chatId);
      setMessages(loaded.messages || []);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Could not open this conversation.");
    }
  }

  function newChat(categoryId = "") {
    clearPendingFiles();
    setChatId(null);
    setMessages([]);
    setNewChatCategoryId(categoryId);
    setSidebarOpen(false);
    setDraft("");
  }

  function changeConnection(id: string) {
    setConnectionId(id);
  }

  async function submit(event?: FormEvent) {
    event?.preventDefault();
    const content = draft.trim();
    const uploadedFiles = pendingFiles.flatMap((item) => item.uploaded ? [item.uploaded] : []);
    if ((!content && uploadedFiles.length === 0) || !connectionId || sending || pendingFiles.some((item) => item.uploading)) return;

    setDraft("");
    setSending(true);
    setNotice(null);
    try {
      const request = await providerRegistry.current.get(connectionId).sendMessage(
        chatId,
        { content, files: uploadedFiles, model: modelId || undefined },
        { categoryId: newChatCategoryId || null },
      );
      setChatId(request.chatId);
      setMessages((current) => [...current, request]);
      clearPendingFiles();
      await poll(request.messageId, request.chatId);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Message could not be sent.");
    } finally {
      setSending(false);
    }
  }

  async function poll(messageId: string, targetChatId: string) {
    while (true) {
      await new Promise((resolve) => window.setTimeout(resolve, 650));
      const request = await expectJson<Message>(await fetch(`${apiBase}/v1/messages/${messageId}`));
      setMessages((current) => current.map((message) =>
        message.messageId === messageId ? request : message,
      ));
      if (!terminalStatuses.has(request.status)) continue;
      if (request.status === "Completed" && request.reply) {
        setMessages((current) => {
          const withoutNestedReply = current.map((message) =>
            message.messageId === messageId ? { ...request, reply: undefined } : message,
          );
          return withoutNestedReply.some((message) => message.messageId === request.reply?.messageId)
            ? withoutNestedReply
            : [...withoutNestedReply, request.reply as Message];
        });
      }
      await loadChats();
      setChatId(targetChatId);
      break;
    }
  }

  async function replay(messageId: string) {
    if (sending) return;
    setSending(true);
    setNotice(null);
    try {
      const request = await expectJson<Message>(await fetch(`${apiBase}/v1/messages/${messageId}/replay`, {
        method: "POST",
      }));
      setMessages((current) => [...current, request]);
      await poll(request.messageId, request.chatId);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Message could not be replayed.");
    } finally {
      setSending(false);
    }
  }

  async function createCategory(event: FormEvent) {
    event.preventDefault();
    const name = newCategoryName.trim();
    if (!name) return;
    try {
      const category = await expectJson<Category>(await fetch(`${apiBase}/v1/categories`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name }),
      }));
      await loadCategories();
      setCategoryFilter(category.categoryId);
      setNewChatCategoryId(category.categoryId);
      setNewCategoryName("");
      setShowCategoryForm(false);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Group could not be created.");
    }
  }

  async function deleteCategory(category: Category) {
    if (!window.confirm(`Delete the group '${category.name}'? Conversations will be kept.`)) return;
    try {
      await expectJsonOrEmpty(await fetch(`${apiBase}/v1/categories/${category.categoryId}`, {
        method: "DELETE",
      }));
      if (categoryFilter === category.categoryId) setCategoryFilter("all");
      if (newChatCategoryId === category.categoryId) setNewChatCategoryId("");
      await Promise.all([loadCategories(), loadChats()]);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Group could not be deleted.");
    }
  }

  async function assignCategory(categoryId: string) {
    if (!activeChat) {
      setNewChatCategoryId(categoryId);
      return;
    }
    try {
      await expectJson<Chat>(await fetch(`${apiBase}/v1/chats/${activeChat.chatId}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ categoryId: categoryId || null }),
      }));
      await loadChats();
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Conversation could not be moved.");
    }
  }

  function selectCategory(id: string) {
    setCategoryFilter(id);
    if (id !== "all" && id !== "uncategorized") setNewChatCategoryId(id);
  }

  function handleComposerKey(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      void submit();
    }
  }

  function clearPendingFiles() {
    setPendingFiles((current) => {
      current.forEach((item) => item.previewUrl && URL.revokeObjectURL(item.previewUrl));
      return [];
    });
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  async function selectFiles(files: FileList | null) {
    if (!files || !connectionId || !selectedConnection?.capabilities.fileInput) return;
    for (const file of Array.from(files)) {
      if (file.type.startsWith("image/") && !selectedConnection.capabilities.imageInput) {
        setNotice(`${selectedConnection.name} does not support image input.`);
        continue;
      }
      const key = `${Date.now()}-${Math.random()}`;
      const previewUrl = file.type.startsWith("image/") ? URL.createObjectURL(file) : undefined;
      setPendingFiles((current) => [...current, { key, file, previewUrl, uploading: true }]);
      try {
        const uploaded = await providerRegistry.current.get(connectionId).uploadFile(file);
        setPendingFiles((current) => current.map((item) =>
          item.key === key ? { ...item, uploading: false, uploaded } : item,
        ));
      } catch (error) {
        const message = error instanceof Error ? error.message : "Upload failed.";
        setPendingFiles((current) => current.map((item) =>
          item.key === key ? { ...item, uploading: false, error: message } : item,
        ));
      }
    }
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  function removePendingFile(key: string) {
    setPendingFiles((current) => {
      const removed = current.find((item) => item.key === key);
      if (removed?.previewUrl) URL.revokeObjectURL(removed.previewUrl);
      return current.filter((item) => item.key !== key);
    });
  }

  function connectionFor(id: string) {
    return connections.find((connection) => connection.id === id);
  }

  return (
    <div className="app-shell">
      <button className="mobile-menu" onClick={() => setSidebarOpen(true)} aria-label="Open conversations">Menu</button>
      {sidebarOpen && <button className="scrim" onClick={() => setSidebarOpen(false)} aria-label="Close conversations" />}

      <aside className={`sidebar ${sidebarOpen ? "sidebar-open" : ""}`}>
        <div className="brand-row">
          <div className="brand-mark">U</div>
          <div><strong>MEŽS</strong><span>Every AI. One place.</span></div>
          <span className={`health-dot ${online ? "online" : ""}`} title={online ? "API online" : "API offline"} />
        </div>

        <button className="new-chat" onClick={() => newChat()}><span>+</span> New conversation</button>

        <label className="section-label" htmlFor="connection">New messages use</label>
        <div className="connection-picker">
          <div className="connection-avatar">{selectedConnection ? makeInitials(selectedConnection.name) : "AI"}</div>
          <select id="connection" value={connectionId} onChange={(event) => changeConnection(event.target.value)} disabled={loading || sending}>
            {connections.map((connection) => <option key={connection.id} value={connection.id}>{connection.name}</option>)}
          </select>
        </div>

        {selectedConnection?.supportsModels && (
          <label className="model-picker">
            <span>Model</span>
            <select value={modelId} onChange={(event) => setModelId(event.target.value)} disabled={modelsLoading || sending}>
              {models.length > 0
                ? models.map((model, index) => <option key={model.id || `default-${index}`} value={model.id || ""}>{model.name}</option>)
                : <option value="">{modelsLoading ? "Loading models..." : "Default"}</option>}
            </select>
          </label>
        )}

        <div className="groups-heading">
          <span className="section-label">Groups</span>
          <button onClick={() => setShowCategoryForm((value) => !value)} aria-label="Create group">+</button>
        </div>
        {showCategoryForm && (
          <form className="category-form" onSubmit={(event) => void createCategory(event)}>
            <input autoFocus value={newCategoryName} onChange={(event) => setNewCategoryName(event.target.value)} placeholder="Group name" />
            <button type="submit">Add</button>
          </form>
        )}
        <nav className="category-list" aria-label="Conversation groups">
          <button className={categoryFilter === "all" ? "active" : ""} onClick={() => selectCategory("all")}>
            <i className="all-dot" /><span>All conversations</span><small>{chats.length}</small>
          </button>
          {categories.map((category) => (
            <div className="category-row" key={category.categoryId}>
              <button className={categoryFilter === category.categoryId ? "active" : ""} onClick={() => selectCategory(category.categoryId)}>
                <i style={{ background: category.color }} /><span>{category.name}</span>
                <small>{chats.filter((chat) => chat.categoryId === category.categoryId).length}</small>
              </button>
              <button className="category-delete" onClick={() => void deleteCategory(category)} title="Delete group">x</button>
            </div>
          ))}
          <button className={categoryFilter === "uncategorized" ? "active" : ""} onClick={() => selectCategory("uncategorized")}>
            <i className="empty-dot" /><span>Uncategorized</span><small>{chats.filter((chat) => !chat.categoryId).length}</small>
          </button>
        </nav>

        <div className="history-heading">
          <span className="section-label">Conversations</span>
          <span>{filteredChats.length}</span>
        </div>
        <label className="search-box">
          <span>Search</span>
          <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Filter conversations" />
          {search && <button onClick={() => setSearch("")} aria-label="Clear search">x</button>}
        </label>
        <nav className="chat-list" aria-label="Conversation history">
          {filteredChats.map((chat) => {
            const connection = connectionFor(chat.connectionId);
            const category = categories.find((item) => item.categoryId === chat.categoryId);
            return (
              <button key={chat.chatId} className={chat.chatId === chatId ? "active" : ""} onClick={() => void openChat(chat)}>
                <span className="chat-title">{chat.title}</span>
                <small>{new Date(chat.updatedAt).toLocaleDateString(undefined, { month: "short", day: "numeric" })}</small>
                <span className="chat-label">{connection?.name || chat.connectionId}</span>
                {category && <i className="chat-category" style={{ background: category.color }} title={category.name} />}
              </button>
            );
          })}
          {!loading && filteredChats.length === 0 && <p className="empty-history">No conversations match this view.</p>}
        </nav>

        <div className="sidebar-footer">
          <span className={`status-pill ${online ? "ready" : "offline"}`}>{online ? "API ready" : "API offline"}</span>
          <small>{apiBase || "Connecting..."}</small>
        </div>
      </aside>

      <main className="main-panel">
        <header className="chat-header">
          <div>
            <span className="eyebrow">{selectedProvider?.name || selectedConnection?.name || "MEŽS"}</span>
            <h1>{activeChat?.title || "New conversation"}</h1>
          </div>
          <div className="header-actions">
            <label className="group-picker">
              <span>Group</span>
              <select value={activeChat?.categoryId || newChatCategoryId} onChange={(event) => void assignCategory(event.target.value)}>
                <option value="">Uncategorized</option>
                {categories.map((category) => <option key={category.categoryId} value={category.categoryId}>{category.name}</option>)}
              </select>
            </label>
            {selectedConnection && <div className="provider-chip"><span>{makeInitials(selectedConnection.name)}</span>{selectedConnection.name}</div>}
          </div>
        </header>

        <section className={`conversation ${messages.length === 0 ? "conversation-empty" : ""}`} aria-live="polite">
          {messages.length === 0 ? (
            <div className="welcome">
              <div className="welcome-orbit"><span>U</span></div>
              <p className="eyebrow">YOUR UNIVERSAL AI WORKSPACE</p>
              <h2>One conversation layer across every AI.</h2>
              <p>Bring subscriptions, free sessions, and local models into one interface while MEŽS keeps your history independent from every provider.</p>
              <div className="product-points">
                <article><b>01</b><strong>Connect</strong><p>Use the AI access you already have behind one consistent API.</p></article>
                <article><b>02</b><strong>Continue</strong><p>Keep durable local conversations, even when a guest session forgets them.</p></article>
                <article><b>03</b><strong>Organize</strong><p>Search and group work without depending on a provider's project system.</p></article>
              </div>
            </div>
          ) : messages.map((message) => {
            const messageConnection = connectionFor(message.connectionId);
            const messageModelName = message.model &&
              message.connectionId === selectedConnection?.id
              ? models.find((model) => model.id === message.model)?.name || message.model
              : message.model;
            return (
              <article key={message.messageId} className={`message ${message.role}`}>
                <div className="message-avatar">{message.role === "assistant" ? "U" : "YOU"}</div>
                <div className="message-body">
                  <div className="message-meta">
                    <strong>{message.role === "assistant" ? messageConnection?.name || "Assistant" : "You"}</strong>
                    <span>{new Date(message.createdAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span>
                    {message.model && (
                      <span
                        className="message-model"
                        title={`${message.role === "user" ? "Requested" : "Provider-reported"} model ID: ${message.model}`}
                      >
                        {message.role === "user" ? "Requested: " : "Served: "}{messageModelName}
                      </span>
                    )}
                    {message.status !== "Completed" && <span className={`message-status ${message.status.toLowerCase()}`}>{message.status}</span>}
                  </div>
                  {message.content && <div className="message-content">{message.content}</div>}
                  {message.files?.length > 0 && (
                    <div className="message-files">
                      {message.files.map((file) => file.contentType.startsWith("image/") ? (
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
                  {message.role === "user" && terminalStatuses.has(message.status) && (
                    <button className="replay" onClick={() => void replay(message.messageId)} disabled={sending}>Replay request</button>
                  )}
                </div>
              </article>
            );
          })}
          {sending && messages.length > 0 && <div className="thinking"><i /><i /><i /><span>{selectedModel?.id ? `${selectedModel.name} is thinking...` : "Working through it..."}</span></div>}
          <div ref={endRef} />
        </section>

        <footer className="composer-wrap">
          {notice && <div className="notice"><span>{notice}</span><button onClick={() => setNotice(null)} aria-label="Dismiss">x</button></div>}
          <form className="composer" onSubmit={(event) => void submit(event)}>
            {pendingFiles.length > 0 && (
              <div className="pending-files">
                {pendingFiles.map((item) => (
                  <div className={`pending-file ${item.error ? "failed" : ""}`} key={item.key}>
                    {item.previewUrl ? <img src={item.previewUrl} alt="" /> : <b>FILE</b>}
                    <span>{item.file.name}<small>{item.error || (item.uploading ? "Uploading..." : formatBytes(item.uploaded?.size || item.file.size))}</small></span>
                    <button type="button" onClick={() => removePendingFile(item.key)} aria-label={`Remove ${item.file.name}`}>x</button>
                  </div>
                ))}
              </div>
            )}
            <textarea
              value={draft}
              onChange={(event) => setDraft(event.target.value)}
              onKeyDown={handleComposerKey}
              placeholder={online ? `Message ${selectedConnection?.name || "an AI"}...` : "Start the API to send a message"}
              rows={1}
              disabled={!online || !connectionId}
              aria-label="Message"
            />
            <input
              ref={fileInputRef}
              className="file-input"
              type="file"
              multiple
              onChange={(event) => void selectFiles(event.target.files)}
            />
            <div className="composer-actions">
              <button
                type="button"
                className="attach"
                disabled={!selectedConnection?.capabilities.fileInput || sending}
                title={selectedConnection?.capabilities.fileInput ? "Attach files" : "This connection does not support files"}
                onClick={() => fileInputRef.current?.click()}
              >+</button>
              <span>Enter to send / Shift + Enter for a new line</span>
              <button
                className="send"
                type="submit"
                disabled={(!draft.trim() && !pendingFiles.some((item) => item.uploaded)) || pendingFiles.some((item) => item.uploading) || sending || !online}
                aria-label="Send message"
              >^</button>
            </div>
          </form>
          <small className="disclaimer">Responses come directly from the selected web AI connection.</small>
        </footer>
      </main>
    </div>
  );
}

async function expectJsonOrEmpty(response: Response) {
  if (!response.ok) {
    const body = await response.json().catch(() => ({ error: response.statusText }));
    throw new Error(body.error || `Request failed (${response.status})`);
  }
}
