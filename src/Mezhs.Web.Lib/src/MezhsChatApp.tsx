import { FormEvent, KeyboardEvent, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Check, Pencil, Plus, Settings, Trash2, X } from "lucide-react";
import { apiJson, apiJsonOrEmpty, useApiAvailability } from "./api";
import { modelActivityLabel } from "./ChatSurface";
import { ConversationViewport } from "./ConversationViewport";
import { useConversationPending } from './useConversationPending';
import { useConversationDrafts } from "./useConversationDrafts";
import { MarkdownContent } from "./MarkdownContent";
import { ConnectionModelPicker } from "./ConnectionModelPicker";
import { ChatProviderRegistry } from "./providers/registry";
import { useAutoResizeTextArea } from "./useAutoResizeTextArea";
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

type GroupDeleteDialog = {
  category: Category;
  chatIds: string[];
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

// Bound each request. A client-side timeout does not undo a server-side write.
async function groupDeleteRequest<T>(request: (signal: AbortSignal) => Promise<T>): Promise<T> {
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(), 12_000);
  try {
    return await request(controller.signal);
  } catch (error) {
    if (controller.signal.aborted)
      throw new Error("Request timed out. The server may still have completed the operation.");
    throw error;
  } finally {
    window.clearTimeout(timeout);
  }
}

// Category IDs are case-insensitive in ChatStore.
function sameCategoryId(left: string | null | undefined, right: string) {
  return left?.toLowerCase() === right.toLowerCase();
}

function matchesChatQuery(chat: Chat, query: string, connectionName?: string, categoryName?: string) {
  const normalized = query.trim().toLocaleLowerCase();
  if (!normalized) return true;
  return [chat.title, connectionName, categoryName]
    .some((value) => value?.toLocaleLowerCase().includes(normalized));
}

export type MezhsChatAppProps = {
  apiBaseUrl: string;
};

export default function MezhsChatApp({ apiBaseUrl }: MezhsChatAppProps) {
  const apiBase = apiBaseUrl.replace(/\/$/, "");
  const apiAvailability = useApiAvailability(apiBase);
  const online = apiAvailability === "online";
  const [connections, setConnections] = useState<Connection[]>([]);
  const [connectionId, setConnectionId] = useState("");
  const [models, setModels] = useState<ConnectionModel[]>([]);
  const [modelId, setModelId] = useState("");
  const [modelsLoading, setModelsLoading] = useState(false);
  const [categories, setCategories] = useState<Category[]>([]);
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [newChatCategoryId, setNewChatCategoryId] = useState("");
  const [newCategoryName, setNewCategoryName] = useState("");
  const [editingCategory, setEditingCategory] = useState<"new" | Category | null>(null);
  const [savingCategory, setSavingCategory] = useState(false);
  const [search, setSearch] = useState("");
  const [chats, setChats] = useState<Chat[]>([]);
  const [chatId, setChatId] = useState<string | null>(null);
  const [messages, setMessages] = useState<Message[]>([]);
  const [newChatSequence, setNewChatSequence] = useState(0);
  const [historyLoadedChatId, setHistoryLoadedChatId] = useState<string | null>(null);
  const [pendingFiles, setPendingFiles] = useState<PendingFile[]>([]);
  const [loading, setLoading] = useState(true);
  const [notice, setNotice] = useState<string | null>(null);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [showConversationManager, setShowConversationManager] = useState(false);
  const [managerSearch, setManagerSearch] = useState("");
  const [selectedChatIds, setSelectedChatIds] = useState<Set<string>>(new Set());
  const [deletingChats, setDeletingChats] = useState(false);
  const [groupDeleteDialog, setGroupDeleteDialog] = useState<GroupDeleteDialog | null>(null);
  const [groupDeleteOpening, setGroupDeleteOpening] = useState(false);
  const [groupDeleteBusy, setGroupDeleteBusy] = useState(false);
  const [groupDeleteError, setGroupDeleteError] = useState<string | null>(null);
  const [groupDeleteUnverified, setGroupDeleteUnverified] = useState(false);
  const [chatMenuId, setChatMenuId] = useState<string | null>(null);
  const [chatRenameDraft, setChatRenameDraft] = useState("");
  const selectionEpoch = useRef(0);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const composerRef = useRef<HTMLTextAreaElement>(null);
  const providerRegistry = useRef(new ChatProviderRegistry());
  const openChatMenuRef = useRef<HTMLDivElement | null>(null);
  const deletedChatIdsRef = useRef(new Set<string>());
  const groupDeleteModalRef = useRef<HTMLDialogElement>(null);
  const groupFilterRef = useRef<HTMLSelectElement>(null);
  const restoreGroupFilterFocusRef = useRef(false);

  const selectedConnection = useMemo(
    () => connections.find((connection) => connection.id === connectionId),
    [connections, connectionId],
  );
  const selectedProvider = providerRegistry.current.tryGet(connectionId);
  const activeChat = chats.find((chat) => chat.chatId === chatId);
  const selectedGroup = categories.find((category) => sameCategoryId(category.categoryId, categoryFilter));
  const draftKey = chatId ? `chat:${chatId}` : `new:${newChatSequence}:${connectionId}`;
  const { draft, setDraft, clearDraft, moveDraft, restoreDraft } = useConversationDrafts(draftKey);
  const { pending: sending, start: startSend, transfer: transferSend, finish: finishSend } = useConversationPending(draftKey);
  const activeMessage = [...messages].reverse().find((message) =>
    message.role === "user" && !terminalStatuses.has(message.status));
  const busyLabel = modelActivityLabel(activeMessage?.activity, activeMessage?.activityDetail);

  const filteredChats = useMemo(() => {
    return chats.filter((chat) => {
      if (categoryFilter === "uncategorized" && chat.categoryId) return false;
      if (categoryFilter !== "all" && categoryFilter !== "uncategorized" && !sameCategoryId(chat.categoryId, categoryFilter)) return false;
      const connection = connections.find((item) => item.id === chat.connectionId);
      const category = categories.find((item) => sameCategoryId(chat.categoryId, item.categoryId));
      return matchesChatQuery(chat, search, connection?.name, category?.name);
    });
  }, [chats, connections, categories, categoryFilter, search]);

  const managedChats = useMemo(() => filteredChats.filter((chat) => {
    const connection = connections.find((item) => item.id === chat.connectionId);
    const category = categories.find((item) => sameCategoryId(chat.categoryId, item.categoryId));
    return matchesChatQuery(chat, managerSearch, connection?.name, category?.name);
  }), [filteredChats, connections, categories, managerSearch]);

  const allManagedChatsSelected = managedChats.length > 0 &&
    managedChats.every((chat) => selectedChatIds.has(chat.chatId));

  useEffect(() => {
  let cancelled = false;
  void Promise.all([
    apiJson<Connection[]>(apiBase, "/v1/connections"),
    apiJson<Chat[]>(apiBase, "/v1/chats"),
    apiJson<Category[]>(apiBase, "/v1/categories"),
  ])
    .then(([available, chatValues, categoryValues]) => {
      if (cancelled) return;
      providerRegistry.current.configure(apiBase, available);
      setConnections(available);
      setChats(chatValues);
      setCategories(categoryValues);
      if (available.length) setConnectionId(available[0].id);
      setNotice(null);
    })
    .catch((error) => {
      if (!cancelled)
        setNotice(error instanceof Error ? error.message : "Could not reach MEŽS.");
    })
    .finally(() => {
      if (!cancelled) setLoading(false);
    });
  return () => { cancelled = true; };
}, [apiBase]);

useEffect(() => () => providerRegistry.current.dispose(), []);

  // showModal makes the rest of the page inert and contains keyboard focus.
  useLayoutEffect(() => {
    if (!groupDeleteDialog) return;
    const modal = groupDeleteModalRef.current;
    if (!modal) return;
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    modal.showModal();
    return () => {
      if (modal.open) modal.close();
      if (restoreGroupFilterFocusRef.current) {
        restoreGroupFilterFocusRef.current = false;
        groupFilterRef.current?.focus();
      } else if (previousFocus?.isConnected) {
        previousFocus.focus();
      }
    };
  }, [Boolean(groupDeleteDialog)]);

  useEffect(() => {
    if (!chatMenuId) return;
    const dismiss = (event: MouseEvent) => {
      const target = event.target;
      if (!(target instanceof Node) || openChatMenuRef.current?.contains(target)) return;
      if (target instanceof Element && target.closest(".chat-options")) return;
      setChatMenuId(null);
    };
    document.addEventListener("click", dismiss);
    return () => document.removeEventListener("click", dismiss);
  }, [chatMenuId]);

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

  async function loadChats(base = apiBase) {
    try {
      const loaded = await apiJson<Chat[]>(base, "/v1/chats");
      setChats(loaded.filter((chat) => !deletedChatIdsRef.current.has(chat.chatId)));
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Could not load conversations.");
    }
  }

  async function loadCategories(base = apiBase) {
    setCategories(await apiJson<Category[]>(base, "/v1/categories"));
  }

  async function openChat(chat: Chat) {
    const epoch = ++selectionEpoch.current;
    setHistoryLoadedChatId(null);
    clearPendingFiles();
    setChatMenuId(null);
    setChatId(chat.chatId);
    setMessages([]);
    setConnectionId(chat.connectionId);
    setSidebarOpen(false);
    try {
      const loaded = await providerRegistry.current.get(chat.connectionId).getChat(chat.chatId);
      if (selectionEpoch.current === epoch) setMessages(loaded.messages || []);
    } catch (error) {
      if (selectionEpoch.current === epoch)
        setNotice(error instanceof Error ? error.message : "Could not open this conversation.");
    } finally {
      if (selectionEpoch.current === epoch) setHistoryLoadedChatId(chat.chatId);
    }
  }

  function newChat() {
    ++selectionEpoch.current;
    clearPendingFiles();
    setChatMenuId(null);
    setChatId(null);
    setMessages([]);
    setNewChatSequence((current) => current + 1);
    setNewChatCategoryId(categoryFilter === "all" || categoryFilter === "uncategorized" ? "" : categoryFilter);
    setSidebarOpen(false);
  }

  function changeConnection(id: string) {
    // Invalidate callbacks from the old connection even when a chat is open.
    if (id !== connectionId) ++selectionEpoch.current;
    setConnectionId(id);
  }

  // Both composer sends and model-authored actions use exactly one provider
  // dispatch and one synchronous conversation-scoped in-flight lock.
  async function sendTurn(content: string, files: ApiFile[], fromComposer: boolean) {
    if ((!content.trim() && files.length === 0) || !connectionId || sending || !online) return;
    const epoch = selectionEpoch.current;
    const targetChatId = chatId;
    const targetConnectionId = connectionId;
    const submittedKey = draftKey;
    let pendingKey = submittedKey;
    if (!startSend(pendingKey)) return;
    if (fromComposer) clearDraft(submittedKey, content);
    let accepted = false;
    setNotice(null);
    try {
      const request = await providerRegistry.current.get(targetConnectionId).sendMessage(
        targetChatId,
        { content, files, model: modelId || undefined },
        { categoryId: newChatCategoryId || null },
      );
      accepted = true;
      if (!targetChatId) {
        if (fromComposer) moveDraft(submittedKey, `chat:${request.chatId}`, "");
        transferSend(pendingKey, `chat:${request.chatId}`);
        pendingKey = `chat:${request.chatId}`;
      }
      if (selectionEpoch.current === epoch) {
        setChatId(request.chatId);
        setHistoryLoadedChatId(request.chatId);
        setMessages((current) => [...current, request]);
        if (fromComposer) clearPendingFiles();
      }
      await poll(request.messageId, request.chatId, epoch);
    } catch (error) {
      if (fromComposer && !accepted) restoreDraft(submittedKey, content);
      if (selectionEpoch.current === epoch)
        setNotice(error instanceof Error ? error.message : "Message could not be sent.");
    } finally {
      finishSend(pendingKey);
    }
  }

  function submit(event?: FormEvent) {
    event?.preventDefault();
    if (pendingFiles.some((item) => item.uploading)) return;
    const files = pendingFiles.flatMap((item) => item.uploaded ? [item.uploaded] : []);
    void sendTurn(draft, files, true);
  }

  function issueNewTurn(source: Message, sourceEpoch: number, content: string) {
    // A detached old message must never target the newly selected conversation.
    if (sourceEpoch !== selectionEpoch.current || !chatId ||
        source.chatId !== chatId || source.connectionId !== connectionId ||
        !online || sending || !!activeMessage || source.role !== "assistant" || source.status !== "Completed" ||
        ["agent-runtime", "command-result"].includes(source.origin.toLowerCase()) ||
        !messages.some((message) => message.messageId === source.messageId &&
          message.content === source.content && message.status === "Completed")) return;
    void sendTurn(content, [], false);
  }

  async function poll(messageId: string, targetChatId: string, epoch: number) {
    while (true) {
      await new Promise((resolve) => window.setTimeout(resolve, 650));
      const request = await apiJson<Message>(apiBase, `/v1/messages/${messageId}`);
      if (selectionEpoch.current === epoch) {
        setMessages((current) => current.map((message) =>
          message.messageId === messageId ? request : message,
        ));
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
      }
      if (!terminalStatuses.has(request.status)) continue;
      await loadChats();
      if (selectionEpoch.current === epoch) setChatId(targetChatId);
      break;
    }
  }

  async function replay(messageId: string) {
    const epoch = selectionEpoch.current;
    const pendingKey = draftKey;
    if (!startSend(pendingKey)) return;
    setNotice(null);
    try {
      const request = await apiJson<Message>(apiBase, `/v1/messages/${messageId}/replay`, {
        method: "POST",
      });
      if (selectionEpoch.current === epoch)
        setMessages((current) => [...current, request]);
      await poll(request.messageId, request.chatId, epoch);
    } catch (error) {
      if (selectionEpoch.current === epoch)
        setNotice(error instanceof Error ? error.message : "Message could not be replayed.");
    } finally {
      finishSend(pendingKey);
    }
  }

  function beginCategoryEdit() {
    setNewCategoryName(selectedGroup?.name ?? "");
    setEditingCategory(selectedGroup ?? "new");
  }

  async function saveCategory(event: FormEvent) {
    event.preventDefault();
    const name = newCategoryName.trim();
    if (!editingCategory || !name || savingCategory) return;
    setSavingCategory(true);
    try {
      let createdCategory: Category | null = null;
      if (editingCategory === "new") {
        createdCategory = await apiJson<Category>(apiBase, "/v1/categories", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ name }),
        });
      } else {
        await apiJson<Category>(apiBase,
          `/v1/categories/${encodeURIComponent(editingCategory.categoryId)}`, {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ name }),
          });
      }
      await loadCategories();
      if (createdCategory) selectCategory(createdCategory.categoryId);
      setNewCategoryName("");
      setEditingCategory(null);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Group could not be saved.");
    } finally {
      setSavingCategory(false);
    }
  }

  async function promptDeleteCategory(category: Category) {
    if (groupDeleteOpening || groupDeleteBusy || deletingChats) return;
    restoreGroupFilterFocusRef.current = false;
    setGroupDeleteOpening(true);
    setNotice(null);
    try {
      // Core chat inventory includes Agent-backed chats on the Agent host.
      const current = await groupDeleteRequest((signal) => apiJson<Chat[]>(apiBase, "/v1/chats", { signal }));
      setChats(current.filter((chat) => !deletedChatIdsRef.current.has(chat.chatId)));
      setGroupDeleteDialog({
        category,
        chatIds: current.filter((chat) => sameCategoryId(chat.categoryId, category.categoryId))
          .map((chat) => chat.chatId),
      });
      setGroupDeleteError(null);
      setGroupDeleteUnverified(false);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Could not load group conversations.");
    } finally {
      setGroupDeleteOpening(false);
    }
  }

  function closeGroupDeleteDialog() {
    if (groupDeleteBusy) return;
    setGroupDeleteDialog(null);
    setGroupDeleteError(null);
    setGroupDeleteUnverified(false);
  }

  async function deleteCategory(mode: "unassign" | "delete") {
    const dialog = groupDeleteDialog;
    if (!dialog || groupDeleteBusy || groupDeleteUnverified || deletingChats) return;
    setGroupDeleteBusy(true);
    setGroupDeleteError(null);
    let chatsDeleted = false;
    try {
      const current = await groupDeleteRequest((signal) => apiJson<Chat[]>(apiBase, "/v1/chats", { signal }));
      const ids = current.filter((chat) => sameCategoryId(chat.categoryId, dialog.category.categoryId))
        .map((chat) => chat.chatId);
      // Never silently delete a different set than the one shown in the dialog.
      if (ids.length !== dialog.chatIds.length ||
          ids.some((id) => !dialog.chatIds.includes(id))) {
        setChats(current.filter((chat) => !deletedChatIdsRef.current.has(chat.chatId)));
        setGroupDeleteDialog({ ...dialog, chatIds: ids });
        setGroupDeleteError("Group membership changed. Review the updated count and choose again.");
        return;
      }

      if (mode === "delete" && ids.length > 0) {
        // The Agent host routes these same requests through Agent-first/Core-second cleanup.
        if (ids.length === 1) {
          await groupDeleteRequest((signal) => apiJsonOrEmpty(apiBase, `/v1/chats/${encodeURIComponent(ids[0])}`, {
            method: "DELETE", signal,
          }));
        } else {
          await groupDeleteRequest((signal) => apiJsonOrEmpty(apiBase, "/v1/chats", {
            method: "DELETE", signal,
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ chatIds: ids }),
          }));
        }
        chatsDeleted = true;
        const removed = new Set(ids);
        ids.forEach((id) => deletedChatIdsRef.current.add(id));
        setChats((existing) => existing.filter((chat) => !removed.has(chat.chatId)));
        if (chatId && removed.has(chatId)) newChat();
        setSelectedChatIds((selected) => new Set([...selected].filter((id) => !removed.has(id))));
        setShowConversationManager(false);
      }

      // Keep this existing endpoint/behavior for "Unassign conversations".
      // Delete the group only after the chat deletion request has succeeded.
      await groupDeleteRequest((signal) => apiJsonOrEmpty(apiBase,
        `/v1/categories/${encodeURIComponent(dialog.category.categoryId)}`, {
          method: "DELETE", signal,
        }));

      if (categoryFilter === dialog.category.categoryId) setCategoryFilter("all");
      if (newChatCategoryId === dialog.category.categoryId) setNewChatCategoryId("");
      restoreGroupFilterFocusRef.current = true;
      setGroupDeleteDialog(null);
      const noun = ids.length === 1 ? "conversation" : "conversations";
      setNotice(mode === "delete"
        ? `Group '${dialog.category.name}' and ${ids.length} ${noun} deleted.`
        : `Group '${dialog.category.name}' deleted; ${ids.length} ${noun} unassigned.`);
      try {
        const [currentCategories, currentChats] = await Promise.all([
          groupDeleteRequest((signal) => apiJson<Category[]>(apiBase, "/v1/categories", { signal })),
          groupDeleteRequest((signal) => apiJson<Chat[]>(apiBase, "/v1/chats", { signal })),
        ]);
        setCategories(currentCategories);
        setChats(currentChats.filter((chat) => !deletedChatIdsRef.current.has(chat.chatId)));
      } catch (error) {
        setNotice(error instanceof Error ? error.message : "Group deleted, but the list could not be refreshed.");
      }
    } catch (error) {
      const detail = error instanceof Error ? error.message : "Request failed.";
      // The server may have committed even if the response was lost. Read both
      // owners' Core views before offering a retry or claiming any outcome.
      try {
        const [current, currentCategories] = await Promise.all([
          groupDeleteRequest((signal) => apiJson<Chat[]>(apiBase, "/v1/chats", { signal })),
          groupDeleteRequest((signal) => apiJson<Category[]>(apiBase, "/v1/categories", { signal })),
        ]);
        // The server may have committed the delete before the response was lost.
        // Reconcile against all remaining Core chats, not category membership.
        const currentChatIds = new Set(current.map((chat) => chat.chatId));
        const removed = new Set(dialog.chatIds.filter((id) => !currentChatIds.has(id)));
        removed.forEach((id) => deletedChatIdsRef.current.add(id));
        setChats(current.filter((chat) => !deletedChatIdsRef.current.has(chat.chatId)));
        if (chatId && removed.has(chatId)) newChat();
        setSelectedChatIds((selected) => new Set([...selected].filter((id) => !removed.has(id))));
        setCategories(currentCategories);
        if (!currentCategories.some((category) => category.categoryId === dialog.category.categoryId)) {
          if (categoryFilter === dialog.category.categoryId) setCategoryFilter("all");
          if (newChatCategoryId === dialog.category.categoryId) setNewChatCategoryId("");
          restoreGroupFilterFocusRef.current = true;
          setGroupDeleteDialog(null);
          setNotice(chatsDeleted
            ? `Group '${dialog.category.name}' is removed; conversation deletion succeeded.`
            : `Group '${dialog.category.name}' is removed; verify conversations because the earlier request outcome was uncertain.`);
        } else {
          setGroupDeleteDialog({
            ...dialog,
            chatIds: current.filter((chat) => sameCategoryId(chat.categoryId, dialog.category.categoryId))
              .map((chat) => chat.chatId),
          });
          setGroupDeleteError(chatsDeleted
            ? `Conversations were deleted, but the group remains: ${detail} Retry to remove the group.`
            : `Could not complete group deletion: ${detail} Review the current count before retrying.`);
        }
      } catch {
        setGroupDeleteUnverified(true);
        setGroupDeleteError(`Could not verify group deletion: ${detail} Close this dialog and refresh the page before retrying.`);
      }
    } finally {
      setGroupDeleteBusy(false);
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
        !window.confirm(`Delete ${description} (${ids.length} ${noun})? Conversation transcripts (and Agent state in the Agent host) will be removed from active storage. Previously imported legacy chat files, if any, remain as backups. Historical Executor records and stored files may remain. Running Executor shells are not stopped by chat deletion.`)) return;

    setDeletingChats(true);
    setChatMenuId(null);
    setNotice(null);
    try {
      if (ids.length === 1) {
      await apiJsonOrEmpty(apiBase, `/v1/chats/${encodeURIComponent(ids[0])}`, { method: "DELETE" });
    } else {
      await apiJsonOrEmpty(apiBase, "/v1/chats", {
        method: "DELETE",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ chatIds: ids }),
      });
    }

      const deleted = new Set(ids);
      ids.forEach((id) => deletedChatIdsRef.current.add(id));
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

  async function assignCategory(targetChatId: string, categoryId: string) {
    try {
      await apiJson<Chat>(apiBase, `/v1/chats/${encodeURIComponent(targetChatId)}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ categoryId: categoryId || null }),
      });
      setChats((current) => current.map((chat) =>
        chat.chatId === targetChatId ? { ...chat, categoryId: categoryId || undefined } : chat));
      setChatMenuId(null);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Conversation could not be moved.");
    }
  }

  async function renameChat(chat: Chat, value: string) {
    const title = value.trim();
    if (!title || title === chat.title) return;
    try {
      await apiJson(apiBase, `/v1/chats/${encodeURIComponent(chat.chatId)}`, {
        method: "PATCH", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title }),
      });
      setChats((current) => current.map((item) =>
        item.chatId === chat.chatId ? { ...item, title } : item));
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Conversation could not be renamed.");
    }
  }

  function selectCategory(id: string) {
    setCategoryFilter(id);
    setNewChatCategoryId(id === "all" || id === "uncategorized" ? "" : id);
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

        <ConnectionModelPicker
          connections={connections}
          connectionId={connectionId}
          models={models}
          modelId={modelId}
          onConnectionChange={changeConnection}
          onModelChange={setModelId}
          connectionDisabled={loading || sending}
          modelDisabled={sending}
          modelsLoading={modelsLoading}
        />


        <div className="base-group-filter">
          {editingCategory ? (
            <form className="base-group-editor" onSubmit={(event) => void saveCategory(event)}>
              <input autoFocus value={newCategoryName} maxLength={100}
                aria-label={editingCategory === "new" ? "New group name" : "Rename group"}
                placeholder={editingCategory === "new" ? "New group name" : "Group name"}
                disabled={savingCategory}
                onChange={(event) => setNewCategoryName(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === "Escape" && !savingCategory) setEditingCategory(null);
                }} />
              <button type="submit" aria-label="Save group" title="Save group"
                disabled={!newCategoryName.trim() || savingCategory}>
                <Check aria-hidden="true" size={17} strokeWidth={2} />
              </button>
              <button type="button" aria-label="Cancel editing group" title="Cancel"
                disabled={savingCategory} onClick={() => setEditingCategory(null)}>
                <X aria-hidden="true" size={17} strokeWidth={2} />
              </button>
            </form>
          ) : (
            <>
              <select ref={groupFilterRef} aria-label="Filter conversations by group"
                value={categoryFilter} onChange={(event) => selectCategory(event.target.value)}>
                <option value="all">All groups ({chats.length})</option>
                {categories.map((category) => (
                  <option key={category.categoryId} value={category.categoryId}>
                    {category.name} ({chats.filter((chat) => sameCategoryId(chat.categoryId, category.categoryId)).length})
                  </option>
                ))}
                <option value="uncategorized">
                  Uncategorized ({chats.filter((chat) => !chat.categoryId).length})
                </option>
              </select>
              <button type="button" onClick={beginCategoryEdit}
                aria-label={selectedGroup ? `Rename group ${selectedGroup.name}` : "Create group"}
                title={selectedGroup ? "Rename group" : "Create group"}>
                {selectedGroup
                  ? <Pencil aria-hidden="true" size={17} strokeWidth={1.8} />
                  : <Plus aria-hidden="true" size={18} strokeWidth={2} />}
              </button>
              <button type="button" disabled={!selectedGroup || groupDeleteOpening || deletingChats}
                aria-label={selectedGroup ? `Delete group ${selectedGroup.name}` : "Delete group"}
                title={selectedGroup ? "Delete group" : "Select a group to delete"}
                onClick={() => { if (selectedGroup) void promptDeleteCategory(selectedGroup); }}>
                <Trash2 aria-hidden="true" size={17} strokeWidth={1.8} />
              </button>
            </>
          )}
        </div>

        <div className="base-sidebar-tools">
          <input type="search" aria-label="Search conversations" placeholder="Search conversations"
            value={search} onChange={(event) => setSearch(event.target.value)} />
          <button type="button" className="base-sidebar-new" aria-label="New conversation"
            title="New conversation" onClick={() => newChat()}>
            <Plus aria-hidden="true" size={18} strokeWidth={2} />
          </button>
          <button type="button" aria-label="Manage conversations" title="Manage conversations"
            onClick={openConversationManager}>
            <Settings aria-hidden="true" size={18} strokeWidth={1.8} />
          </button>
        </div>
        <span className="section-label">Conversations · {filteredChats.length} of {chats.length}</span>
        <nav className="chat-list" aria-label="Conversation history">
          {filteredChats.map((chat) => {
            const connection = connectionFor(chat.connectionId);
            return (
              <div className="chat-row" key={chat.chatId}
                ref={chatMenuId === chat.chatId ? openChatMenuRef : null}
                onBlur={(event) => {
                  if (chatMenuId === chat.chatId && !event.currentTarget.contains(event.relatedTarget))
                    setChatMenuId((current) => current === chat.chatId ? null : current);
                }}
                onKeyDown={(event) => {
                  if (event.key === "Escape" && chatMenuId === chat.chatId) {
                    event.stopPropagation();
                    setChatMenuId(null);
                  }
                }}>
                <button className={`chat-open ${chat.chatId === chatId ? "active" : ""}`}
                  onClick={() => void openChat(chat)}>
                  <span className="chat-title" title={chat.title}>{chat.title}</span>
                  <span className="base-chat-meta">
                    <span className="base-chat-context">{connection?.name || chat.connectionId}</span>
                    <time dateTime={chat.updatedAt} title={new Date(chat.updatedAt).toLocaleString()}>
                      {new Date(chat.updatedAt).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" })}
                    </time>
                  </span>
                </button>
                <button type="button" className="chat-options"
                  aria-label={`Options for ${chat.title}`}
                  aria-expanded={chatMenuId === chat.chatId}
                  onClick={() => {
                    if (chatMenuId === chat.chatId) setChatMenuId(null);
                    else {
                      setChatRenameDraft(chat.title);
                      setChatMenuId(chat.chatId);
                    }
                  }}>⋯</button>
                {chatMenuId === chat.chatId && (
                  <div className="chat-options-menu">
                    <input type="text" className="base-chat-menu-title" autoFocus
                      aria-label={`Rename ${chat.title}`} title="Rename conversation"
                      maxLength={200} value={chatRenameDraft}
                      onChange={(event) => setChatRenameDraft(event.target.value)}
                      onBlur={(event) => { void renameChat(chat, event.currentTarget.value); }}
                      onKeyDown={(event) => {
                        if (event.key === "Enter") {
                          event.preventDefault();
                          event.currentTarget.blur();
                        }
                      }} />
                    <select className="base-chat-menu-group" value={chat.categoryId || ""}
                      aria-label={`Group for ${chat.title}`}
                      onChange={(event) => void assignCategory(chat.chatId, event.target.value)}>
                      <option value="">Uncategorized</option>
                      {categories.map((category) => (
                        <option key={category.categoryId} value={category.categoryId}>{category.name}</option>
                      ))}
                    </select>
                    <button type="button" className="base-chat-menu-delete"
                      disabled={deletingChats}
                      onClick={() => void deleteConversations([chat], `'${chat.title}'`)}>Delete</button>
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

      {groupDeleteDialog && (
        <dialog ref={groupDeleteModalRef} className="group-delete-dialog" aria-modal="true"
          aria-labelledby="group-delete-title" aria-describedby="group-delete-summary"
          onCancel={(event) => {
            event.preventDefault();
            closeGroupDeleteDialog();
          }}
          onClick={(event) => {
            if (event.target !== event.currentTarget) return;
            const bounds = event.currentTarget.getBoundingClientRect();
            if (event.clientX < bounds.left || event.clientX > bounds.right ||
                event.clientY < bounds.top || event.clientY > bounds.bottom)
              closeGroupDeleteDialog();
          }}>
            <h2 id="group-delete-title">Delete group</h2>
            <p id="group-delete-summary">
              Group <strong>{groupDeleteDialog.category.name}</strong> contains{" "}
              <strong>{groupDeleteDialog.chatIds.length}</strong>{" "}
              {groupDeleteDialog.chatIds.length === 1 ? "conversation" : "conversations"}.
              Choose what happens to its conversations, including Agent conversations if present.
            </p>
            <div className="group-delete-choices">
              <button type="button" disabled={groupDeleteBusy || groupDeleteUnverified}
                onClick={() => void deleteCategory("unassign")}>
                <strong>Unassign conversations</strong>
                <span>Delete only the group. Keep all conversations without a group.</span>
              </button>
              <button type="button" className="destructive" disabled={groupDeleteBusy || groupDeleteUnverified}
                onClick={() => void deleteCategory("delete")}>
                <strong>Delete conversations</strong>
                <span>Delete the group and all its conversations permanently.</span>
              </button>
            </div>
            <p className="group-delete-disclosure">
              Deletion removes conversation transcripts and associated Agent state from active storage.
              Previously imported legacy chat files may remain as backups. Historical Executor records
              and stored files may remain. Running Executor shells are not guaranteed to stop.
            </p>
            {groupDeleteError && <p className="group-delete-error" role="alert">{groupDeleteError}</p>}
            <footer>
              <button type="button" autoFocus disabled={groupDeleteBusy}
                onClick={closeGroupDeleteDialog}>Cancel</button>
            </footer>
        </dialog>
      )}

      {showConversationManager && (
        <div className="dialog-backdrop" onClick={() => setShowConversationManager(false)}>
          <section className="conversation-manager" role="dialog" aria-modal="true"
            aria-labelledby="conversation-manager-title" aria-describedby="conversation-manager-summary"
            onClick={(event) => event.stopPropagation()}
            onKeyDown={(event) => {
              if (event.key === "Escape") setShowConversationManager(false);
            }}>
            <header>
              <div>
                <h2 id="conversation-manager-title">Manage conversations</h2>
                <p id="conversation-manager-summary">
                  {managedChats.length} of {filteredChats.length} shown · {chats.length} total conversations
                </p>
              </div>
            </header>
            <label className="manager-filter">
              Filter displayed conversations
              <input type="search" autoFocus autoComplete="off"
                value={managerSearch} onChange={(event) => setManagerSearch(event.target.value)}
                placeholder="Title, connection, group" />
            </label>
            <div className="manager-selection">
              <label>
                <input type="checkbox" checked={allManagedChatsSelected}
                  disabled={managedChats.length === 0 || deletingChats}
                  onChange={selectAllManagedChats} />
                Select all shown
              </label>
              <span>{managedChats.filter((chat) => selectedChatIds.has(chat.chatId)).length} selected</span>
            </div>
            <div className="manager-list">
              {managedChats.map((chat) => {
                const connection = connectionFor(chat.connectionId);
                return (
                  <div className="manager-row" key={chat.chatId}>
                    <label>
                      <input type="checkbox" disabled={deletingChats}
                        checked={selectedChatIds.has(chat.chatId)}
                        onChange={() => toggleManagedChat(chat.chatId)} />
                      <span>
                        <strong title={chat.title}>{chat.title}</strong>
                        <small>{connection?.name || chat.connectionId} ·
                          {new Date(chat.updatedAt).toLocaleDateString()}</small>
                      </span>
                    </label>
                    <button type="button" className="delete-one" disabled={deletingChats}
                      aria-label={`Delete ${chat.title}`}
                      onClick={() => void deleteConversations([chat], `'${chat.title}'`)}>Delete</button>
                  </div>
                );
              })}
              {managedChats.length === 0 &&
                <p className="manager-empty">No conversations match the current filters.</p>}
            </div>
            <footer>
              <button type="button" className="danger-action"
                disabled={deletingChats || !managedChats.some((chat) => selectedChatIds.has(chat.chatId))}
                onClick={() => void deleteConversations(
                  managedChats.filter((chat) => selectedChatIds.has(chat.chatId)),
                  "selected conversations",
                )}>Delete selected</button>
              <button type="button" className="danger-action"
                disabled={managedChats.length === 0 || deletingChats}
                onClick={() => void deleteConversations(managedChats, "all shown conversations")}>
                Delete all shown
              </button>
              <button type="button" className="secondary-action"
                onClick={() => setShowConversationManager(false)}>Close</button>
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
            {selectedConnection && <div className="provider-chip"><span>{makeInitials(selectedConnection.name)}</span>{selectedConnection.name}</div>}
          </div>
        </header>

        <ConversationViewport conversationKey={draftKey} messages={messages} historyReady={!chatId || historyLoadedChatId === chatId} busy={sending} empty={messages.length === 0}>
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
            const messageEpoch = selectionEpoch.current;
            const messageConnection = connectionFor(message.connectionId);
            const messageModelName = message.model &&
              message.connectionId === selectedConnection?.id
              ? models.find((model) => model.id === message.model)?.name || message.model
              : message.model;
            return (
              <article key={message.messageId} className={`message ${message.role}`}>
                <div className="message-avatar">{message.role === "assistant" ? "M" : "YOU"}</div>
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
                  {message.content && <div className="message-content"><MarkdownContent content={message.content}
                      verbatim={message.role === "user"}
                      onIssueNewTurn={chatId && message.chatId === chatId &&
                        message.connectionId === connectionId && message.role === "assistant" &&
                        message.status === "Completed" &&
                        !["agent-runtime", "command-result"].includes(message.origin.toLowerCase()) &&
                        !sending && !activeMessage && online
                        ? (content) => issueNewTurn(message, messageEpoch, content)
                        : undefined}
                    /></div>}
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
                  {message.analysis && (
                    <details className="message-analysis">
                      <summary>Model analysis</summary>
                      <pre>{message.analysis}</pre>
                    </details>
                  )}
                  {message.role === "user" && terminalStatuses.has(message.status) && (
                    <button className="replay" onClick={() => void replay(message.messageId)} disabled={sending}>Replay request</button>
                  )}
                </div>
              </article>
            );
          })}
          {sending && messages.length > 0 && <div className="thinking"><i /><i /><i /><span>{busyLabel}</span></div>}
        </ConversationViewport>

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

