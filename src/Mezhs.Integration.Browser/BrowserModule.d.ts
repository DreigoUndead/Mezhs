interface MezhsBrowserPromptRequest {
  prompt: string;
  newChat?: boolean;
  chatUrl?: string | null;
  workspace?: string | null;
  filePaths?: readonly string[] | null;
}

interface MezhsBrowserArtifact {
  url: string;
  name: string;
  contentType?: string | null;
  localPath?: string | null;
}

interface MezhsBrowserSendResult {
  ok: boolean;
  text?: string;
  error?: string;
  chatUrl?: string;
  artifacts?: MezhsBrowserArtifact[];
  [key: string]: unknown;
}

interface MezhsBrowserWindow {
  loadURL(url: string): Promise<unknown>;
  isVisible(): boolean;
  show(): void;
  focus(): void;
  hide(): void;
  webContents: any;
}

interface MezhsBrowserSendContext {
  window: MezhsBrowserWindow;
  request: MezhsBrowserPromptRequest;
  sleep(ms: number): Promise<void>;
}

interface MezhsBrowserInitializationContext {
  window: MezhsBrowserWindow;
  session: any;
  sleep(ms: number): Promise<void>;
}

interface MezhsBrowserWebRequestContext {
  target: string;
  headers: Headers;
}

interface MezhsBrowserModule {
  readonly name: string;
  readonly homeUrl: string;

  isAuthorized?(window: MezhsBrowserWindow): Promise<boolean>;
  afterInitialize?(context: MezhsBrowserInitializationContext): Promise<void>;
  prepareWebRequest?(context: MezhsBrowserWebRequestContext): Promise<void>;
  sendPrompt(context: MezhsBrowserSendContext): Promise<MezhsBrowserSendResult>;
}

declare const module: { exports: MezhsBrowserModule };
declare function require(name: string): any;
