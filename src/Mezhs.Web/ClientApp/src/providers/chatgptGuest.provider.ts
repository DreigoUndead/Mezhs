import { ApiChatProvider } from "./apiChatProvider";
import { ChatProviderModule } from "./contracts";

class ChatGptGuestProvider extends ApiChatProvider {
  readonly name = "ChatGPT Guest";
}

export const chatProviderModule: ChatProviderModule = {
  types: ["chatgpt-web"],
  create: (connection, apiBase) => new ChatGptGuestProvider(connection, apiBase),
};
