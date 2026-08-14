using Mezhs.Browser;

namespace Mezhs.Browser.WebView2;

public sealed class ChatGptWebView2Transport : IChatBrowserTransport
{
    private ChatGptWebViewClient? _client;

    public string Name => "ChatGPT WebView2 (Windows backup)";

    public async Task InitializeAsync(
        BrowserTransportOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _client = await ChatGptWebViewClient.StartAsync(
            Path.GetFullPath(options.ProfileDirectory),
            authPath: null,
            options.ShowBrowser);
    }

    public async Task<ChatTransportResponse> SendPromptAsync(
        BrowserPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = _client
            ?? throw new InvalidOperationException("WebView2 transport is not initialized.");
        var result = await client.SendPromptViaPageAsync(request.Prompt);
        return new ChatTransportResponse(result.Ok, result.Text, result.Error);
    }

    public Task<BrowserWebResponse> SendWebRequestAsync(
        BrowserWebRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The backup ChatGPT WebView2 transport does not implement authenticated web requests.");

    public Task ShowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _client = null;
    }
}
