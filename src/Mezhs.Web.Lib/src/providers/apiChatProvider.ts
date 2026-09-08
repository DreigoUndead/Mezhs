import {
  ApiFile,
  Chat,
  ChatMessageInput,
  ChatProvider,
  ChatResponse,
  Connection,
  ConnectionModel,
  CreateChatOptions,
  DownloadedFile,
  FileInput,
  SendOptions,
  UploadedFile,
  UploadOptions,
} from "./contracts";

export class ApiChatProvider implements ChatProvider {
  readonly name: string;

  constructor(
    readonly connection: Connection,
    protected readonly apiBase: string,
  ) {
    this.name = connection.name;
  }

  async initialize(): Promise<void> {
    if (!this.connection.requiresLogin) return;
    await expectJson(await fetch(
      `${this.apiBase}/v1/connections/${encodeURIComponent(this.connection.id)}/login`,
      { method: "POST" },
    ));
  }

  async getModels(): Promise<ConnectionModel[]> {
    if (!this.connection.supportsModels) return [];
    const response = await fetch(
      `${this.apiBase}/v1/connections/${encodeURIComponent(this.connection.id)}/models`,
    );
    const models = response.status === 401
      ? [{ id: null, name: "Default" }]
      : await expectJson<ConnectionModel[]>(response);
    const configured = this.connection.defaultModel?.trim();
    return configured && !models.some((model) => model.id === configured)
      ? [...models, { id: configured, name: configured }]
      : models;
  }

  async getChat(chatId: string): Promise<Chat> {
    const [chat, messages] = await Promise.all([
      expectJson<Chat>(await fetch(`${this.apiBase}/v1/chats/${encodeURIComponent(chatId)}`)),
      expectJson<Chat["messages"]>(await fetch(
        `${this.apiBase}/v1/chats/${encodeURIComponent(chatId)}/messages`,
      )),
    ]);
    return { ...chat, messages: messages || [] };
  }

  async createChat(options?: CreateChatOptions): Promise<Chat> {
    return expectJson<Chat>(await fetch(`${this.apiBase}/v1/chats`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        connectionId: this.connection.id,
        categoryId: options?.categoryId || null,
      }),
    }));
  }

  async sendMessage(
    chatId: string | null,
    message: ChatMessageInput,
    options?: SendOptions,
  ): Promise<ChatResponse> {
    const body = {
      connectionId: this.connection.id,
      chatId: chatId || undefined,
      content: message.content,
      model: message.model || undefined,
      categoryId: chatId ? undefined : options?.categoryId || null,
      fileIds: (message.files || []).map(file => file.fileId),
    };
    return expectJson<ChatResponse>(await fetch(`${this.apiBase}/v1/messages`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }));
  }

  async uploadFile(file: FileInput, _options?: UploadOptions): Promise<UploadedFile> {
    const form = new FormData();
    form.append("connectionId", this.connection.id);
    form.append("file", file, file.name);
    return expectJson<UploadedFile>(await fetch(`${this.apiBase}/v1/files`, {
      method: "POST",
      body: form,
    }));
  }

  async downloadFile(_chatId: string, fileId: string): Promise<DownloadedFile> {
    const file = await expectJson<ApiFile>(await fetch(
      `${this.apiBase}/v1/files/${encodeURIComponent(fileId)}`,
    ));
    const response = await fetch(`${this.apiBase}${file.downloadUrl}`);
    if (!response.ok) throw new Error(`Download failed (${response.status}).`);
    return { file, content: await response.blob() };
  }

  dispose(): void {}
}

export async function expectJson<T = unknown>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => ({ error: response.statusText }));
    throw new Error(body.error || `Request failed (${response.status})`);
  }
  return response.json() as Promise<T>;
}