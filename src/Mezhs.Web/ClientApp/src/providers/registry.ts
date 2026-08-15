import { ApiChatProvider } from "./apiChatProvider";
import { ChatProvider, Connection } from "./contracts";

export class ChatProviderRegistry {
  private readonly providers = new Map<string, ChatProvider>();

  configure(apiBase: string, connections: Connection[]) {
    this.dispose();
    for (const connection of connections)
      this.providers.set(connection.id, new ApiChatProvider(connection, apiBase));
  }

  get(connectionId: string): ChatProvider {
    const provider = this.providers.get(connectionId);
    if (!provider) throw new Error(`Chat connection '${connectionId}' is unavailable.`);
    return provider;
  }

  tryGet(connectionId: string): ChatProvider | undefined {
    return this.providers.get(connectionId);
  }

  dispose() {
    for (const provider of this.providers.values()) provider.dispose();
    this.providers.clear();
  }
}
