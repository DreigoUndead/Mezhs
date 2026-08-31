import { FormEvent, KeyboardEvent, useEffect, useMemo, useRef, useState } from "react";
import { ChatProviderRegistry } from "./providers/registry";
import { useAutoResizeTextArea } from "./useAutoResizeTextArea";
import type {
  ApiFile,
  Chat,
  ChatMessage as Message,
  Connection,
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

function matchesChatQuery(chat: Chat, query: string, connectionName?: string, categoryName?: string) {
  const normalized = query.trim().toLocaleLowerCase();
  if (!normalized) return true;
  return [chat.title, connectionName, categoryName]
    .some((value) => value?.toLocaleLowerCase().includes(normalized));
}

async function expectJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => ({ error: response.statusText }));
    throw new Error(body.error || `Request failed (${response.status})`);
  }
  return response.json() as Promise<T>;
}

export type MezhsChatAppProps = {
  apiBaseUrl: string;
};

export default function MezhsChatApp({ apiBaseUrl }: MezhsChatAppProps) {
  const apiBase = apiBaseUrl.replace(/\/$/, "");
  const [connections, setConnections] = useState<Connection[]>([]);
  const [connectionId, setConnectionId] = useState("");
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
  const [loginId, setLoginId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [showConversationManager, setShowConversationManager] = useState(false);
  const [managerSearch, setManagerSearch] = useState("");
  const [selectedChatIds, setSelectedChatIds] = useState<Set<string>>(new Set());
  const [deletingChats, setDeletingChats] = useState(false);
  const [chatMenuId, setChatMenuId] = useState<string | null>(null);
  const endRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const composerRef = useRef<HTMLTextAreaElement>(null);
  const providerRegistry = useRef(new ChatProviderRegistry());

  const selectedConnection = useMemo(
    () => connections.find((connection) => connection.id === connectionId),
    [connections, connectionId],
  );
  const selectedProvider = providerRegistry.current.tryGet(connectionId);
  const activeChat = chats.find((chat) => chat.chatId === chatId);

  const filteredChats = useMemo(() => {
    return chats.filter((chat) => {
      if (categoryFilter === "uncategorized" && chat.categoryId) return false;
      if (categoryFilter !== "all" && categoryFilter !== "uncategorized" && chat.categoryId !== categoryFilter) return false;
      const connection = connections.find((item) => item.id === chat.connectionId);
      const category = categories.find((item) => item.categoryId === chat.categoryId);
      return matchesChatQuery(chat, search, connection?.name, category?.name);
    });
  }, [chats, connections, categories, categoryFilter, search]);

  const managedChats = useMemo(() => filteredChats.filter((chat) => {
    const connection = connections.find((item) => item.id === chat.connectionId);
    const category = categories.find((item) => item.categoryId === chat.categoryId);
    return matchesChatQuery(chat, managerSearch, connection?.name, category?.name);
  }), [filteredChats, connections, categories, managerSearch]);

  const allManagedChatsSelected = managedChats.length > 0 &&
    managedChats.every((chat) => selectedChatIds.has(chat.chatId));

  useEffect(() => {
    void (async () => {
      try {
        const [healthResponse, connectionResponse, chatResponse, categoryResponse] = await Promise.all([
          fetch(`${apiBase}/health`),
          fetch(`${apiBase}/v1/connections`),
          fetch(`${apiBase}/v1/chats`),
          fetch(`${apiBase}/v1/categories`),
        ]);
        setOnline(healthResponse.ok);
        const available = await expectJson<Connection[]>(connectionResponse);
        providerRegistry.current.configure(apiBase, available);
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
  }, [apiBase]);

  useEffect(() => () => providerRegistry.current.dispose(), []);

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
    setChatMenuId(null);
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
    setChatMenuId(null);
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
        { content, files: uploadedFiles },
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

  async function login(connection: Connection) {
    setLoginId(connection.id);
    setNotice("Complete authorization in the browser window. MEŽS will save this session.");
    try {
      await providerRegistry.current.get(connection.id).initialize();
      setNotice(`${connection.name} is authorized and ready.`);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Authorization failed.");
    } finally {
      setLoginId(null);
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

  function openConversationManager() {
    setChatMenuId(null);
    setManagerSearch("");
    setSelectedChatIds(new Set());
    setShowConversationManager(true);
  }

  function toggleManagedChat(id: string) {
    setSelectedChatIds((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function selectAllManagedChats() {
    setSelectedChatIds((current) => {
      const next = new Set(current);
      if (managedChats.every((chat) => next.has(chat.chatId))) {
        managedChats.forEach((chat) => next.delete(chat.chatId));
      } else {
        managedChats.forEach((chat) => next.add(chat.chatId));
      }
      return next;
    });
  }

  async function deleteConversations(targets: Chat[], description: string, requireConfirmation = true) {
    const ids = [...new Set(targets.map((chat) => chat.chatId))];
    if (ids.length === 0 || deletingChats) return;
    const noun = ids.length === 1 ? "conversation" : "conversations";
    if (requireConfirmation &&
        !window.confirm(`Delete ${description}? This permanently removes ${ids.length} ${noun} from MEŽS.`)) return;

    setDeletingChats(true);
    setChatMenuId(null);
    setNotice(null);
    try {
      const response = ids.length === 1
        ? await fetch(`${apiBase}/v1/chats/${encodeURIComponent(ids[0])}`, { method: "DELETE" })
        : await fetch(`${apiBase}/v1/chats`, {
            method: "DELETE",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ chatIds: ids }),
          });
      await expectJsonOrEmpty(response);

      const deleted = new Set(ids);
      setChats((current) => current.filter((chat) => !deleted.has(chat.chatId)));
      if (chatId && deleted.has(chatId)) newChat();
      setSelectedChatIds(new Set());
      setShowConversationManager(false);
      setNotice(`${ids.length} ${noun} deleted.`);
      await loadChats();
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Conversations could not be deleted.");
    } finally {
      setDeletingChats(false);
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

  useAutoResizeTextArea(composerRef, draft);

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
          <div className="brand-mark">M</div>
          <div><strong>MEŽS</strong><span>Every AI. One place.</span></div>
          <span className={`health-dot ${online ? "online" : ""}`} title={online ? "API online" : "API offline"} />
        </div>

        <button className="new-chat" onClick={() => newChat()}><span>+</span> New conversation</button>

        <label className="section-label" htmlFor="connection">New messages use</label>
        <div className="connection-picker">
          <div className="connection-avatar">{selectedConnection ? makeInitials(selectedConnection.name) : "AI"}</div>
          <select id="connection" value={connectionId} onChange={(event) => changeConnection(event.target.value)} disabled={loading}>
            {connections.map((connection) => <option key={connection.id} value={connection.id}>{connection.name}</option>)}
          </select>
        </div>

        {selectedConnection?.requiresLogin && (
          <button className="login-button" disabled={loginId === selectedConnection.id} onClick={() => void login(selectedConnection)}>
            {loginId === selectedConnection.id ? "Waiting for authorization..." : "Open login window"}
          </button>
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
          <div>
            <span>{filteredChats.length}</span>
            <button onClick={openConversationManager} disabled={loading || sending || filteredChats.length === 0}>Manage</button>
          </div>
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
              <div className="chat-row" key={chat.chatId}>
                <button className={`chat-open ${chat.chatId === chatId ? "active" : ""}`} onClick={() => void openChat(chat)}>
                  <span className="chat-title">{chat.title}</span>
                  <small>{new Date(chat.updatedAt).toLocaleDateString(undefined, { month: "short", day: "numeric" })}</small>
                  <span className="chat-label">{connection?.name || chat.connectionId}</span>
                  {category && <i className="chat-category" style={{ background: category.color }} title={category.name} />}
                </button>
                <button
                  className="chat-options"
                  aria-label={`Conversation options for ${chat.title}`}
                  aria-haspopup="menu"
                  aria-expanded={chatMenuId === chat.chatId}
                  onClick={() => setChatMenuId((current) => current === chat.chatId ? null : chat.chatId)}
                >...</button>
                {chatMenuId === chat.chatId && (
                  <div className="chat-options-menu" role="menu">
                    <button
                      role="menuitem"
                      disabled={deletingChats}
                      onClick={() => void deleteConversations([chat], `'${chat.title}'`, false)}
                    >Delete</button>
                  </div>
                )}
              </div>
            );
          })}
          {!loading && filteredChats.length === 0 && <p className="empty-history">No conversations match this view.</p>}
        </nav>

        <div className="sidebar-footer">
          <span className={`status-pill ${online ? "ready" : "offline"}`}>{online ? "API ready" : "API offline"}</span>
          <small>{apiBase || "Connecting..."}</small>
        </div>
      </aside>

      {showConversationManager && (
        <div className="dialog-backdrop">
          <section className="conversation-manager" role="dialog" aria-modal="true" aria-labelledby="conversation-manager-title">
            <header>
              <div>
                <span className="eyebrow">Conversation management</span>
                <h2 id="conversation-manager-title">Delete conversations</h2>
                <p>Showing {managedChats.length} of {filteredChats.length} conversations from the current sidebar filters.</p>
              </div>
              <button className="dialog-close" onClick={() => setShowConversationManager(false)} aria-label="Close conversation manager">×</button>
            </header>

            <div className="manager-filter">
              <label htmlFor="conversation-delete-filter">Filter conversations to delete</label>
              <input
                id="conversation-delete-filter"
                autoFocus
                value={managerSearch}
                onChange={(event) => setManagerSearch(event.target.value)}
                placeholder="Filter by title, connection, or group"
              />
              {managerSearch && <button type="button" onClick={() => setManagerSearch("")} aria-label="Clear delete filter">x</button>}
            </div>

            <div className="manager-selection">
              <button onClick={selectAllManagedChats} disabled={managedChats.length === 0}>
                {allManagedChatsSelected ? "Clear shown selection" : "Select all shown"}
              </button>
              <span>{selectedChatIds.size} selected</span>
            </div>

            <div className="manager-list">
              {managedChats.map((chat) => {
                const connection = connectionFor(chat.connectionId);
                return (
                  <div className="manager-row" key={chat.chatId}>
                    <label>
                      <input
                        type="checkbox"
                        checked={selectedChatIds.has(chat.chatId)}
                        onChange={() => toggleManagedChat(chat.chatId)}
                      />
                      <span><strong>{chat.title}</strong><small>{connection?.name || chat.connectionId}</small></span>
                    </label>
                    <button
                      className="delete-one"
                      disabled={deletingChats}
                      onClick={() => void deleteConversations([chat], `'${chat.title}'`)}
                    >Delete</button>
                  </div>
                );
              })}
              {managedChats.length === 0 && <p className="manager-empty">No conversations match this delete filter.</p>}
            </div>

            <footer>
              <button className="secondary-action" onClick={() => setShowConversationManager(false)}>Cancel</button>
              <button
                className="danger-action"
                disabled={selectedChatIds.size === 0 || deletingChats}
                onClick={() => void deleteConversations(
                  filteredChats.filter((chat) => selectedChatIds.has(chat.chatId)),
                  `${selectedChatIds.size} selected conversations`,
                )}
              >Delete selected</button>
              <button
                className="danger-action danger-all"
                disabled={managedChats.length === 0 || deletingChats}
                onClick={() => void deleteConversations(managedChats, "all conversations shown by the delete filter")}
              >Delete all shown</button>
            </footer>
          </section>
        </div>
      )}

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
              <div className="welcome-orbit"><span>M</span></div>
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
            return (
              <article key={message.messageId} className={`message ${message.role}`}>
                <div className="message-avatar">{message.role === "assistant" ? "M" : "YOU"}</div>
                <div className="message-body">
                  <div className="message-meta">
                    <strong>{message.role === "assistant" ? messageConnection?.name || "Assistant" : "You"}</strong>
                    <span>{new Date(message.createdAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span>
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
          {sending && messages.length > 0 && <div className="thinking"><i /><i /><i /><span>Working through it...</span></div>}
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
              ref={composerRef}
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
