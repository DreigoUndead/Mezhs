interface PromptRequest {
  prompt: string;
  newChat?: boolean;
  chatUrl?: string | null;
  filePaths?: readonly string[] | null;
}

interface Artifact {
  url: string;
  name: string;
  contentType?: string | null;
  localPath?: string | null;
}

interface SendResult {
  ok: boolean;
  text?: string;
  error?: string;
  chatUrl?: string;
  artifacts?: Artifact[];
  [key: string]: unknown;
}

interface BrowserWindow {
  loadURL(url: string): Promise<unknown>;
  isVisible(): boolean;
  show(): void;
  focus(): void;
  hide(): void;
  webContents: any;
}

interface SendContext {
  window: BrowserWindow;
  request: PromptRequest;
  sleep(ms: number): Promise<void>;
}

interface InitializationContext {
  window: BrowserWindow;
  session: any;
  sleep(ms: number): Promise<void>;
}

interface OperationContext {
  window: BrowserWindow;
  session: any;
  args: any;
  sleep(ms: number): Promise<void>;
}

type BrowserOperation = (context: OperationContext) => Promise<unknown>;

interface BrowserModule {
  readonly name: string;
  readonly homeUrl: string;
  readonly operations?: Record<string, BrowserOperation>;

  isAuthorized?(window: BrowserWindow): Promise<boolean>;
  afterInitialize?(context: InitializationContext): Promise<void>;
  sendPrompt(context: SendContext): Promise<SendResult>;
}

declare const module: { exports: BrowserModule };
declare function require(name: string): any;
