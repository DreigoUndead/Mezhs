import { ChatProvider, ChatProviderModule, Connection } from "./contracts";

type ProviderModuleExport = { chatProviderModule?: ChatProviderModule };

export class ChatProviderRegistry {
  private readonly modules = new Map<string, ChatProviderModule>();
  private readonly providers = new Map<string, ChatProvider>();

  constructor() {
    const exports = import.meta.globEager<ProviderModuleExport>("./*.provider.ts");
    for (const loaded of Object.values(exports)) {
      const module = loaded.chatProviderModule;
      if (!module) continue;
      for (const type of module.types) {
        if (this.modules.has(type))
          throw new Error(`Duplicate TypeScript chat provider '${type}'.`);
        this.modules.set(type, module);
      }
    }
  }

  configure(apiBase: string, connections: Connection[]) {
    this.dispose();
    for (const connection of connections) {
      const module = this.modules.get(connection.provider);
      if (!module)
        throw new Error(`No TypeScript chat provider for '${connection.provider}'.`);
      this.providers.set(connection.id, module.create(connection, apiBase));
    }
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
