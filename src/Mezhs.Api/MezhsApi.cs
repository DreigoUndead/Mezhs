using System.Text.Json.Serialization;
using Mezhs.Api.Contracts;
using Mezhs.Configuration;
using Mezhs.Integrations;
using Mezhs.Services;
using Microsoft.AspNetCore.Mvc;

namespace Mezhs;

public static class MezhsApi
{
    public static WebApplicationBuilder AddMezhsApi(
        this WebApplicationBuilder builder,
        MezhsOptions options)
    {
        builder.WebHost.UseUrls(options.Server.Listen);
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ChatStore>();
        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddSingleton<FileStore>();
        builder.Services.AddSingleton<IIntegrationHost, IntegrationHost>();
        builder.Services.AddSingleton<IntegrationRegistry>();
        builder.Services.AddSingleton<MessageService>();
        builder.Services.AddHostedService<MessageService>(
            services => services.GetRequiredService<MessageService>());
        return builder;
    }

    public static WebApplication UseMezhsApi(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.Services.GetRequiredService<ChatStore>().Initialize();
        app.Services.GetRequiredService<FileStore>().Initialize();
        return app;
    }

    public static WebApplication MapMezhsApi(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/v1/connections", (IntegrationRegistry integrations) =>
            Results.Ok(integrations.GetConnections()));

        app.MapGet("/v1/connections/{connectionId}/models", async (
            string connectionId,
            IntegrationRegistry integrations,
            CancellationToken cancellationToken) =>
        {
            if (!integrations.TryGet(connectionId, out var integration))
                return Results.NotFound(new { error = $"Connection '{connectionId}' was not found." });
            if (integration.Models is null)
                return Results.BadRequest(new { error = $"Connection '{connectionId}' does not support model selection." });

            try
            {
                var discovered = await integration.Models.GetModelsAsync(cancellationToken);
                var models = new[] { new IntegrationModel(null, "Default") }
                    .Concat(discovered
                        .Where(model => !string.IsNullOrWhiteSpace(model.Id) && !string.IsNullOrWhiteSpace(model.Name))
                        .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                return Results.Ok(models);
            }
            catch (IntegrationAuthorizationRequiredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
            }
        });

        app.MapPost("/v1/files", async (
            HttpRequest request,
            IntegrationRegistry integrations,
            FileStore files,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data is required." });

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

        app.MapGet("/v1/chats", (string? connectionId, ChatService chats) =>
            Results.Ok(chats.GetChats(connectionId)));

        app.MapPost("/v1/chats", (CreateChatRequest request, ChatService chats) =>
        {
            var chat = chats.Create(request);
            return Results.Created($"/v1/chats/{chat.ChatId}", chat);
        });

        app.MapDelete("/v1/chats", ([FromBody] DeleteChatsRequest request, ChatStore chats) =>
            Results.Ok(new { deletedChatIds = chats.DeleteChats(request.ChatIds ?? []) }));

        app.MapDelete("/v1/chats/{chatId}", (string chatId, ChatStore chats) =>
        {
            chats.DeleteChats([chatId]);
            return Results.NoContent();
        });

        app.MapGet("/v1/categories", (ChatStore chats) =>
            Results.Ok(chats.GetCategories()));

        app.MapPost("/v1/categories", (CreateCategoryRequest request, ChatStore chats) =>
        {
            var category = chats.CreateCategory(request.Name);
            return Results.Created($"/v1/categories/{category.CategoryId}", category);
        });

        app.MapPut("/v1/categories/{categoryId}", (
            string categoryId,
            UpdateCategoryRequest request,
            ChatStore chats) => Results.Ok(chats.RenameCategory(categoryId, request.Name)));

        app.MapDelete("/v1/categories/{categoryId}", (string categoryId, ChatStore chats) =>
        {
            chats.DeleteCategory(categoryId);
            return Results.NoContent();
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

        app.MapPost("/v1/connections/{connectionId}/browser", async (
            string connectionId,
            IntegrationRegistry integrations,
            CancellationToken cancellationToken) =>
        {
            if (!integrations.TryGet(connectionId, out var integration))
                return Results.NotFound(new { error = $"Connection '{connectionId}' was not found." });
            if (integration.Login is null)
                return Results.BadRequest(new { error = $"Connection '{connectionId}' does not have an account browser." });

            await integration.Login.OpenBrowserAsync(cancellationToken);
            return Results.Ok(new { connectionId, status = "open" });
        });

        app.MapPost("/v1/messages", (PostMessageRequest request, MessageService messages) =>
        {
            var message = messages.Post(request);
            return Results.Accepted($"/v1/messages/{message.MessageId}", message);
        });

        app.MapPost("/v1/messages/{messageId}/replay", (string messageId, MessageService messages) =>
        {
            var message = messages.Replay(messageId);
            return Results.Accepted($"/v1/messages/{message.MessageId}", message);
        });

        app.MapGet("/v1/messages/{messageId}", (string messageId, MessageService messages) =>
        {
            var message = messages.Get(messageId);
            return message is null
                ? Results.NotFound(new { error = $"Message '{messageId}' was not found." })
                : Results.Ok(message);
        });

        app.MapGet("/v1/chats/{chatId}", (string chatId, ChatService chats) =>
            Results.Ok(chats.Get(chatId)));

        app.MapPatch("/v1/chats/{chatId}", (
            string chatId,
            UpdateChatRequest request,
            ChatStore chats) => Results.Ok(chats.SetChatCategory(chatId, request.CategoryId)));

        app.MapGet("/v1/chats/{chatId}/messages", (string chatId, ChatService chats) =>
            Results.Ok(chats.GetMessages(chatId)));

        return app;
    }
}
