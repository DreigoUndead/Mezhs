import { ApiChatProvider } from "./apiChatProvider";
import { ChatProviderModule } from "./contracts";

class ChatGptFreeProvider extends ApiChatProvider {
  readonly name = "ChatGPT Free";
}

export const chatProviderModule: ChatProviderModule = {
  types: ["chatgpt-web-free"],
  create: (connection, apiBase) => new ChatGptFreeProvider(connection, apiBase),
};
