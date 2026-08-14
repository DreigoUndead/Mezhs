import { ApiChatProvider } from "./apiChatProvider";
import { ChatProviderModule } from "./contracts";

class GeminiGuestProvider extends ApiChatProvider {
  readonly name = "Gemini Guest";
}

export const chatProviderModule: ChatProviderModule = {
  types: ["gemini-web"],
  create: (connection, apiBase) => new GeminiGuestProvider(connection, apiBase),
};
