import { ApiChatProvider } from "./apiChatProvider";
import { ChatProviderModule } from "./contracts";

class ChatGptSubscriptionProvider extends ApiChatProvider {
  readonly name = "ChatGPT Subscription";
}

export const chatProviderModule: ChatProviderModule = {
  types: ["chatgpt-web-subscription"],
  create: (connection, apiBase) => new ChatGptSubscriptionProvider(connection, apiBase),
};
