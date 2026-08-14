import { ApiChatProvider } from "./apiChatProvider";
import { ChatProviderModule } from "./contracts";

class MockChatProvider extends ApiChatProvider {
  readonly name = "Mock";
}

export const chatProviderModule: ChatProviderModule = {
  types: ["mock"],
  create: (connection, apiBase) => new MockChatProvider(connection, apiBase),
};
