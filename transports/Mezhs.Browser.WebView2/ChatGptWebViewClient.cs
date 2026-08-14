using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Mezhs.Browser.WebView2;

internal sealed class ChatGptWebViewClient : IAsyncDisposable
{
    private readonly BrowserForm _form;
    private readonly Thread _uiThread;

    private ChatGptWebViewClient(BrowserForm form, Thread uiThread)
    {
        _form = form;
        _uiThread = uiThread;
    }

    public static Task<ChatGptWebViewClient> StartAsync(
        string profileDirectory,
        string? authPath,
        bool showBrowser)
    {
        var ready = new TaskCompletionSource<ChatGptWebViewClient>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Thread? uiThread = null;
        uiThread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new BrowserForm(profileDirectory, authPath, showBrowser);
            form.Ready.Task.ContinueWith(task =>
            {
                if (task.IsFaulted)
                    ready.TrySetException(task.Exception!.InnerExceptions);
                else if (task.IsCanceled)
                    ready.TrySetCanceled();
                else
                    ready.TrySetResult(new ChatGptWebViewClient(form, uiThread!));
            }, TaskScheduler.Default);

            Application.Run(form);
        })
        {
            IsBackground = true,
            Name = "MEŽS WebView2",
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        return ready.Task;
    }

    public Task<BrowserResponse> SendRequestAsync(
        string endpoint,
        Dictionary<string, object?> body)
    {
        var endpointJson = JsonSerializer.Serialize(endpoint);
        var bodyJson = JsonSerializer.Serialize(body);
        var script = $$"""
            (async () => {
              try {
                const sessionResponse = await fetch("https://chatgpt.com/api/auth/session", {
                  credentials: "include",
                  cache: "no-store"
                });
                const sessionText = await sessionResponse.text();
                let session = null;
                try { session = JSON.parse(sessionText); } catch {}

                if (!sessionResponse.ok || !session?.accessToken) {
                  return {
                    ok: false,
                    status: sessionResponse.status,
                    statusText: "ChatGPT session unavailable",
                    body: sessionText
                  };
                }

                const controller = new AbortController();
                const timeout = setTimeout(() => controller.abort(), 45000);
                const response = await fetch({{endpointJson}}, {
                  method: "POST",
                  credentials: "include",
                  signal: controller.signal,
                  headers: {
                    "Accept": "text/event-stream",
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${session.accessToken}`
                  },
                  body: JSON.stringify({{bodyJson}})
                });

                let responseBody = "";
                let timedOut = false;
                try {
                  const reader = response.body.getReader();
                  const decoder = new TextDecoder();
                  while (true) {
                    const chunk = await reader.read();
                    if (chunk.done) break;
                    responseBody += decoder.decode(chunk.value, { stream: true });
                    if (responseBody.includes("data: [DONE]")) {
                      await reader.cancel();
                      break;
                    }
                  }
                  responseBody += decoder.decode();
                } catch (error) {
                  if (error?.name === "AbortError") timedOut = true;
                  else throw error;
                } finally {
                  clearTimeout(timeout);
                }

                return {
                  ok: response.ok,
                  status: response.status,
                  statusText: response.statusText,
                  contentType: response.headers.get("content-type"),
                  cfMitigated: response.headers.get("cf-mitigated"),
                  body: responseBody,
                  timedOut
                };
              } catch (error) {
                return { ok: false, error: String(error?.stack ?? error) };
              }
            })()
            """;

        return _form.ExecuteAsync(script);
    }

    public Task<PageChatResult> SendPromptViaPageAsync(string prompt)
    {
        var promptJson = JsonSerializer.Serialize(prompt);
        var script = $$"""
            (async () => {
              const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
              const prompt = {{promptJson}};
              const assistantSelector = '[data-message-author-role="assistant"]';
              const beforeCount = document.querySelectorAll(assistantSelector).length;
              let editor = null;
              const editorDeadline = Date.now() + 30000;
              while (!editor && Date.now() < editorDeadline) {
                editor = document.querySelector('#prompt-textarea');
                if (!editor) await sleep(250);
              }

              if (!editor) {
                return {
                  ok: false,
                  error: `Prompt editor was not found at ${location.href}`
                };
              }

              editor.focus();
              if (editor instanceof HTMLTextAreaElement) {
                const setter = Object.getOwnPropertyDescriptor(
                  HTMLTextAreaElement.prototype,
                  'value'
                )?.set;
                setter?.call(editor, prompt);
                editor.dispatchEvent(new InputEvent('input', {
                  bubbles: true,
                  inputType: 'insertText',
                  data: prompt
                }));
              } else {
                document.execCommand('selectAll', false, null);
                document.execCommand('insertText', false, prompt);
                editor.dispatchEvent(new InputEvent('input', {
                  bubbles: true,
                  inputType: 'insertText',
                  data: prompt
                }));
              }

              await sleep(500);
              const sendButton = document.querySelector(
                'button[data-testid="send-button"], button[aria-label*="Send"]'
              );
              if (!sendButton || sendButton.disabled) {
                return {
                  ok: false,
                  error: 'The ChatGPT send button was not available after filling the prompt.'
                };
              }

              sendButton.click();

              const startedDeadline = Date.now() + 30000;
              while (Date.now() < startedDeadline) {
                const messages = document.querySelectorAll(assistantSelector);
                if (messages.length > beforeCount)
                  break;
                await sleep(250);
              }

              let lastText = '';
              let stableSamples = 0;
              const responseDeadline = Date.now() + 180000;
              while (Date.now() < responseDeadline) {
                const messages = document.querySelectorAll(assistantSelector);
                const latest = messages[messages.length - 1];
                const text = latest?.innerText?.trim() ?? '';
                const stopButton = document.querySelector(
                  'button[data-testid="stop-button"], button[aria-label*="Stop"]'
                );

                if (text && text === lastText) stableSamples++;
                else stableSamples = 0;
                lastText = text;

                if (text && !stopButton && stableSamples >= 8)
                  return { ok: true, text };

                await sleep(500);
              }

              return {
                ok: false,
                text: lastText,
                error: lastText
                  ? 'Timed out while waiting for ChatGPT to finish; returning the latest text.'
                  : 'Timed out before ChatGPT produced an assistant message.'
              };
            })()
            """;

        return _form.ExecutePageChatAsync(script);
    }

    public async ValueTask DisposeAsync()
    {
        await _form.CloseAsync();
        if (_uiThread.IsAlive)
            _uiThread.Join(TimeSpan.FromSeconds(5));
    }
}

internal sealed class BrowserForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _profileDirectory;
    private readonly string? _authPath;
    private readonly bool _showBrowser;
    private readonly WebView2Control _webView;
    private bool _allowClose;

    public TaskCompletionSource Ready { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public BrowserForm(string profileDirectory, string? authPath, bool showBrowser)
    {
        _profileDirectory = profileDirectory;
        _authPath = authPath;
        _showBrowser = showBrowser;

        Text = "MEŽS - ChatGPT browser session";
        Width = 1200;
        Height = 850;
        StartPosition = showBrowser
            ? FormStartPosition.CenterScreen
            : FormStartPosition.Manual;
        if (!showBrowser)
            Location = new Point(-32000, -32000);
        Opacity = showBrowser ? 1 : 0;
        ShowInTaskbar = true;

        _webView = new WebView2Control { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, e) =>
        {
            if (_allowClose)
                return;

            e.Cancel = true;
            Hide();
        };
    }

    public Task<BrowserResponse> ExecuteAsync(string script)
    {
        var completion = new TaskCompletionSource<BrowserResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        BeginInvoke(async () =>
        {
            try
            {
                var response = await ExecuteValueAsync<BrowserResponse>(
                    script,
                    TimeSpan.FromSeconds(70));
                completion.TrySetResult(response);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    public Task<PageChatResult> ExecutePageChatAsync(string script)
    {
        var completion = new TaskCompletionSource<PageChatResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        BeginInvoke(async () =>
        {
            try
            {
                var response = await ExecuteValueAsync<PageChatResult>(
                    script,
                    TimeSpan.FromSeconds(220));
                completion.TrySetResult(response);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    private Task<T> ExecuteValueAsync<T>(string script, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = Guid.NewGuid().ToString("N");
        var timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1, (int)timeout.TotalMilliseconds)
        };

        EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
        void Cleanup()
        {
            timer.Stop();
            timer.Dispose();
            if (handler is not null)
                _webView.CoreWebView2.WebMessageReceived -= handler;
        }

        handler = (_, args) =>
        {
            try
            {
                var message = JsonNode.Parse(args.WebMessageAsJson) as JsonObject;
                if (message?["channel"]?.GetValue<string>() != "mezhs" ||
                    message["id"]?.GetValue<string>() != requestId)
                    return;

                Cleanup();
                if (message["error"]?.GetValue<string>() is { } error)
                {
                    completion.TrySetException(new InvalidOperationException(error));
                    return;
                }

                var result = message["result"] is { } resultNode
                    ? resultNode.Deserialize<T>(JsonOptions)
                    : default;
                if (result is null)
                    completion.TrySetException(new InvalidOperationException("WebView returned no result."));
                else
                    completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Cleanup();
                completion.TrySetException(ex);
            }
        };

        timer.Tick += (_, _) =>
        {
            Cleanup();
            completion.TrySetException(new TimeoutException(
                $"WebView JavaScript did not reply within {timeout.TotalSeconds:F0} seconds."));
        };

        _webView.CoreWebView2.WebMessageReceived += handler;
        timer.Start();

        var idJson = JsonSerializer.Serialize(requestId);
        var wrapper = $$"""
            (() => {
              const id = {{idJson}};
              Promise.resolve({{script}})
                .then(result => chrome.webview.postMessage({
                  channel: "mezhs",
                  id,
                  result
                }))
                .catch(error => chrome.webview.postMessage({
                  channel: "mezhs",
                  id,
                  error: String(error?.stack ?? error)
                }));
              return true;
            })()
            """;

        _ = _webView.CoreWebView2.ExecuteScriptAsync(wrapper).ContinueWith(task =>
        {
            if (!task.IsFaulted)
                return;

            BeginInvoke(() =>
            {
                Cleanup();
                completion.TrySetException(task.Exception!.InnerExceptions);
            });
        }, TaskScheduler.Default);

        return completion.Task;
    }

    public Task CloseAsync()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (IsDisposed)
        {
            completion.SetResult();
            return completion.Task;
        }

        BeginInvoke(() =>
        {
            _allowClose = true;
            Close();
            completion.TrySetResult();
        });
        return completion.Task;
    }

    private async Task InitializeAsync()
    {
        try
        {
            Console.WriteLine("WebView2: creating profile and environment...");
            Directory.CreateDirectory(_profileDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _profileDirectory);
            await _webView.EnsureCoreWebView2Async(environment);
            Console.WriteLine("WebView2: runtime initialized.");

            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            if (_authPath is not null)
                await ImportCookiesAsync(_authPath);

            if (_showBrowser)
                ShowForLogin();

            var navigation = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                _webView.NavigationCompleted -= OnNavigationCompleted;
                navigation.TrySetResult();
            }

            _webView.NavigationCompleted += OnNavigationCompleted;
            Console.WriteLine("WebView2: navigating to ChatGPT...");
            _webView.Source = new Uri("https://chatgpt.com/");
            var navigationResult = await Task.WhenAny(navigation.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Console.WriteLine(navigationResult == navigation.Task
                ? $"WebView2: navigation completed at {_webView.Source}."
                : $"WebView2: continuing while ChatGPT remains active at {_webView.Source}.");

            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!await HasSessionAsync())
            {
                if (DateTimeOffset.UtcNow >= deadline && !VisibleForUser())
                {
                    Console.WriteLine("ChatGPT requires interactive login or browser verification.");
                    Console.WriteLine("Complete it in the WebView2 window; the window will hide when ready.");
                    ShowForLogin();
                }

                await Task.Delay(1500);
            }

            Console.WriteLine("WebView2: authenticated ChatGPT session detected.");

            if (!_showBrowser)
                HideFromUser();

            Ready.TrySetResult();
        }
        catch (Exception ex)
        {
            Ready.TrySetException(ex);
        }
    }

    private async Task<bool> HasSessionAsync()
    {
        try
        {
            return await ExecuteValueAsync<bool>("""
                (async () => {
                  try {
                    const response = await fetch("https://chatgpt.com/api/auth/session", {
                      credentials: "include",
                      cache: "no-store"
                    });
                    if (!response.ok) return false;
                    const session = await response.json();
                    return Boolean(session?.accessToken);
                  } catch { return false; }
                })()
                """, TimeSpan.FromSeconds(15));
        }
        catch
        {
            return false;
        }
    }

    private async Task ImportCookiesAsync(string authPath)
    {
        ChatGptAuth? auth;
        try
        {
            auth = JsonSerializer.Deserialize<ChatGptAuth>(
                await File.ReadAllTextAsync(authPath),
                JsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not import auth file: {ex.Message}");
            return;
        }

        var manager = _webView.CoreWebView2.CookieManager;
        var imported = 0;
        foreach (var source in auth?.Cookies ?? [])
        {
            if (string.IsNullOrWhiteSpace(source.Name) ||
                string.IsNullOrWhiteSpace(source.Domain) ||
                IsCloudflareCookie(source.Name))
                continue;

            try
            {
                var cookie = manager.CreateCookie(
                    source.Name,
                    source.Value ?? "",
                    source.Domain,
                    source.Path ?? "/");
                cookie.IsHttpOnly = source.HttpOnly;
                cookie.IsSecure = source.Secure;
                if (source.ExpirationDate is { } expiration)
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)expiration).UtcDateTime;
                manager.AddOrUpdateCookie(cookie);
                imported++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skipped cookie {source.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Imported {imported} ChatGPT cookies into WebView2.");
    }

    private static bool IsCloudflareCookie(string name) =>
        name.StartsWith("__cf", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("cf_", StringComparison.OrdinalIgnoreCase);

    private bool VisibleForUser() => Opacity > 0 && ShowInTaskbar;

    private void ShowForLogin()
    {
        StartPosition = FormStartPosition.CenterScreen;
        Location = new Point(
            Math.Max(0, (Screen.PrimaryScreen!.WorkingArea.Width - Width) / 2),
            Math.Max(0, (Screen.PrimaryScreen.WorkingArea.Height - Height) / 2));
        Opacity = 1;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        Console.WriteLine($"WebView2: login window visible at {Location}, size {Size}.");
    }

    private void HideFromUser()
    {
        Opacity = 0;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        ShowInTaskbar = false;
    }
}

internal sealed record BrowserResponse(
    bool Ok,
    int Status = 0,
    string? StatusText = null,
    string? ContentType = null,
    string? CfMitigated = null,
    string? Body = null,
    string? Error = null,
    bool TimedOut = false);

internal sealed record PageChatResult(
    bool Ok,
    string Text = "",
    string? Error = null);

internal sealed record ChatGptAuth(List<ExportedCookie>? Cookies);

internal sealed record ExportedCookie(
    string Name,
    string? Value,
    string Domain,
    string? Path,
    bool Secure,
    bool HttpOnly,
    double? ExpirationDate);
