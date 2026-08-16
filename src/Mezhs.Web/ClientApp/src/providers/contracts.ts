export type IntegrationCapabilities = {
  fileInput: boolean;
  imageInput: boolean;
  fileOutput: boolean;
  imageOutput: boolean;
};

export type Connection = {
  id: string;
  name: string;
  integration: string;
  requiresLogin: boolean;
  project?: string;
  capabilities: IntegrationCapabilities;
};

export type ApiFile = {
  fileId: string;
  connectionId: string;
  name: string;
  contentType: string;
  size: number;
  source: "User" | "Assistant";
  createdAt: string;
  contentUrl: string;
  downloadUrl: string;
};

export type ChatMessage = {
  messageId: string;
  chatId: string;
  connectionId: string;
  role: "user" | "assistant";
  content: string;
  files: ApiFile[];
  status: "Queued" | "Running" | "Completed" | "Failed" | "Cancelled";
  createdAt: string;
  error?: string;
  replayOfMessageId?: string;
  reply?: ChatMessage;
};

export type Chat = {
  chatId: string;
  connectionId: string;
  categoryId?: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messages?: ChatMessage[];
};

export type CreateChatOptions = { categoryId?: string | null };
export type ChatMessageInput = { content: string; files?: UploadedFile[] };
export type SendOptions = { categoryId?: string | null };
export type FileInput = File;
export type UploadOptions = Record<string, never>;
export type UploadedFile = ApiFile;
export type DownloadedFile = { file: ApiFile; content: Blob };
export type ChatResponse = ChatMessage;

export interface ChatProvider {
  readonly name: string;
  readonly connection: Connection;

  initialize(): Promise<void>;
  getChat(chatId: string): Promise<Chat>;
  createChat(options?: CreateChatOptions): Promise<Chat>;
  sendMessage(
    chatId: string | null,
    message: ChatMessageInput,
    options?: SendOptions,
  ): Promise<ChatResponse>;
  uploadFile(file: FileInput, options?: UploadOptions): Promise<UploadedFile>;
  downloadFile(chatId: string, fileId: string): Promise<DownloadedFile>;
  dispose(): void;
}
