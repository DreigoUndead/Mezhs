// Shared TypeScript contract for provider modules executed by the Electron browser transport.
interface BrowserWindow {
  loadURL(url: string): Promise<unknown>;
  isVisible(): boolean;
  show(): void;
  focus(): void;
  hide(): void;
  webContents: any;
}

interface BrowserPage {
  invoke(operation: string, args?: any): Promise<any>;
}

interface InitializationContext {
  window: BrowserWindow;
  session: any;
  sleep(ms: number): Promise<void>;
}

interface OperationContext {
  window: BrowserWindow;
  session: any;
  page: BrowserPage;
  args: any;
  sleep(ms: number): Promise<void>;
}

interface PageOperationContext {
  args: any;
  sleep(ms: number): Promise<void>;
}

type BrowserOperation = (context: OperationContext) => unknown | Promise<unknown>;
type BrowserPageOperation = (context: PageOperationContext) => unknown | Promise<unknown>;

interface BrowserModule {
  readonly name: string;
  readonly homeUrl: string;
  readonly operations: Record<string, BrowserOperation>;
  readonly pageOperations?: Record<string, BrowserPageOperation>;

  isAuthorized?(window: BrowserWindow): Promise<boolean>;
  afterInitialize?(context: InitializationContext): Promise<void>;
}

declare const module: { exports: BrowserModule };
declare function require(name: string): any;
