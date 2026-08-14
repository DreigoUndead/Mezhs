using System.Text.Json.Serialization;
using Mezhs.Configuration;
using Mezhs.Integrations;
using Mezhs.Models;
using Mezhs.Services;

var configPath = FindConfigPath(GetOption(args, "--config"));
var options = MezhsConfigLoader.Load(configPath);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(options.Server.Listen);
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ChatStore>();
builder.Services.AddSingleton<FileStore>();
builder.Services.AddSingleton<IIntegrationHost, IntegrationHost>();
builder.Services.AddSingleton<IntegrationRegistry>();
builder.Services.AddSingleton<MessageService>();

var app = builder.Build();
app.UseCors();
var store = app.Services.GetRequiredService<ChatStore>();
await store.InitializeAsync();
var fileStore = app.Services.GetRequiredService<FileStore>();
await fileStore.InitializeAsync();

app.MapGet("/", () => Results.Ok(new
{
    name = "MEŽS",
    version = 1,
    endpoints = new[] { "/v1/connections", "/v1/messages", "/v1/chats", "/v1/categories", "/v1/files" }
}));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/v1/connections", (IntegrationRegistry integrations) =>
    Results.Ok(integrations.GetConnections()));

app.MapPost("/v1/files", async (
    HttpRequest request,
    IntegrationRegistry integrations,
    FileStore files,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data is required." });

    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        var connectionId = form["connectionId"].ToString();
        if (string.IsNullOrWhiteSpace(connectionId))
            return Results.BadRequest(new { error = "connectionId is required." });
        var integration = integrations.Get(connectionId);
        if (!integration.Capabilities.FileInput)
            return Results.BadRequest(new { error = $"Connection '{connectionId}' does not support file input." });
        var upload = form.Files.GetFile("file");
        if (upload is null)
            return Results.BadRequest(new { error = "file is required." });
        if (upload.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            !integration.Capabilities.ImageInput)
            return Results.BadRequest(new { error = $"Connection '{connectionId}' does not support image input." });

        await using var content = upload.OpenReadStream();
        var file = await files.CreateAsync(
            connectionId,
            upload.FileName,
            upload.ContentType,
            content,
            FileSource.User,
            cancellationToken);
        return Results.Created($"/v1/files/{file.FileId}", FileStore.ToApi(file));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapGet("/v1/files/{fileId}", (string fileId, FileStore files) =>
{
    var file = files.Get(fileId);
    return file is null
        ? Results.NotFound(new { error = $"File '{fileId}' was not found." })
        : Results.Ok(FileStore.ToApi(file));
});

app.MapGet("/v1/files/{fileId}/content", (
    string fileId,
    bool? download,
    FileStore files) =>
{
    var file = files.Get(fileId);
    if (file is null)
        return Results.NotFound(new { error = $"File '{fileId}' was not found." });
    return Results.File(
        files.GetContentPath(file),
        file.ContentType,
        download == true ? file.Name : null,
        enableRangeProcessing: true);
});

app.MapGet("/v1/chats", (string? connectionId, ChatStore chats) =>
    Results.Ok(chats.GetChats(connectionId).Select(chat => new
    {
        chat.ChatId,
        chat.ConnectionId,
        chat.CategoryId,
        chat.CreatedAt,
        chat.UpdatedAt,
        title = chats.GetMessages(chat.ChatId)
            .FirstOrDefault(message => message.Role == "user")?.Content ?? "New chat"
    })));

app.MapPost("/v1/chats", async (
    CreateChatRequest request,
    IntegrationRegistry integrations,
    ChatStore chats,
    CancellationToken cancellationToken) =>
{
    try
    {
        integrations.Get(request.ConnectionId);
        var chat = await chats.CreateChatAsync(
            request.ConnectionId,
            request.CategoryId,
            cancellationToken);
        return Results.Created($"/v1/chats/{chat.ChatId}", new
        {
            chat.ChatId,
            chat.ConnectionId,
            chat.CategoryId,
            chat.CreatedAt,
            chat.UpdatedAt,
            title = "New chat"
        });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapGet("/v1/categories", (ChatStore chats) =>
    Results.Ok(chats.GetCategories()));

app.MapPost("/v1/categories", async (
    CreateCategoryRequest request,
    ChatStore chats,
    CancellationToken cancellationToken) =>
{
    try
    {
        var category = await chats.CreateCategoryAsync(request.Name, cancellationToken);
        return Results.Created($"/v1/categories/{category.CategoryId}", category);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPut("/v1/categories/{categoryId}", async (
    string categoryId,
    UpdateCategoryRequest request,
    ChatStore chats,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await chats.RenameCategoryAsync(
            categoryId,
            request.Name,
            cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapDelete("/v1/categories/{categoryId}", async (
    string categoryId,
    ChatStore chats,
    CancellationToken cancellationToken) =>
{
    try
    {
        await chats.DeleteCategoryAsync(categoryId, cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapPost("/v1/connections/{connectionId}/login", async (
    string connectionId,
    IntegrationRegistry integrations,
    CancellationToken cancellationToken) =>
{
    if (!integrations.TryGet(connectionId, out var integration))
        return Results.NotFound(new { error = $"Connection '{connectionId}' was not found." });
    if (integration.Login is null)
        return Results.BadRequest(new { error = $"Connection '{connectionId}' does not support login." });

    await integration.Login.LoginAsync(cancellationToken);
    return Results.Ok(new { connectionId, status = "ready" });
});

app.MapPost("/v1/messages", async (
    PostMessageRequest request,
    MessageService messages,
    CancellationToken cancellationToken) =>
{
    try
    {
        var message = await messages.PostAsync(request, cancellationToken);
        return Results.Accepted($"/v1/messages/{message.MessageId}", message);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapPost("/v1/messages/{messageId}/replay", async (
    string messageId,
    MessageService messages,
    CancellationToken cancellationToken) =>
{
    try
    {
        var message = await messages.ReplayAsync(messageId, cancellationToken);
        return Results.Accepted($"/v1/messages/{message.MessageId}", message);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapGet("/v1/messages/{messageId}", (
    string messageId,
    MessageService messages) =>
{
    var message = messages.Get(messageId);
    return message is null
        ? Results.NotFound(new { error = $"Message '{messageId}' was not found." })
        : Results.Ok(message);
});

app.MapGet("/v1/chats/{chatId}", (string chatId, ChatStore chats) =>
{
    var chat = chats.GetChat(chatId);
    return chat is null
        ? Results.NotFound(new { error = $"Chat '{chatId}' was not found." })
        : Results.Ok(chat);
});

app.MapPatch("/v1/chats/{chatId}", async (
    string chatId,
    UpdateChatRequest request,
    ChatStore chats,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await chats.SetChatCategoryAsync(
            chatId,
            request.CategoryId,
            cancellationToken));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapGet("/v1/chats/{chatId}/messages", (string chatId, ChatStore chats) =>
{
    if (chats.GetChat(chatId) is null)
        return Results.NotFound(new { error = $"Chat '{chatId}' was not found." });
    return Results.Ok(chats.GetMessages(chatId));
});

Console.WriteLine($"MEŽS config: {configPath}");
Console.WriteLine($"MEŽS listening: {options.Server.Listen}");
await app.RunAsync();

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string FindConfigPath(string? configuredPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath))
        return Path.GetFullPath(configuredPath);

    var currentCandidate = Path.GetFullPath("mezhs.yaml");
    if (File.Exists(currentCandidate))
        return currentCandidate;

    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Mezhs.sln")))
        {
            var repositoryCandidate = Path.Combine(directory.FullName, "mezhs.yaml");
            if (File.Exists(repositoryCandidate))
                return repositoryCandidate;
        }
        directory = directory.Parent;
    }

    var outputCandidate = Path.Combine(AppContext.BaseDirectory, "mezhs.yaml");
    if (File.Exists(outputCandidate))
        return outputCandidate;

    return currentCandidate;
}
