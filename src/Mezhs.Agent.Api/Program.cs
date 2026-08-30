using System.Text.Json.Serialization;
using Mezhs.Agent;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Policy;
using Mezhs.Agent.Services;
using Mezhs.Api.Contracts;

var configPath = FindConfigPath(GetOption(args, "--config"));
var options = AgentConfigLoader.Load(configPath);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(options.Listen.ToString());
builder.WebHost.UseWebRoot(FindAgentWebRoot());
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<AgentStore>();
builder.Services.AddSingleton<PolicyRegistry>();
builder.Services.AddSingleton<PolicyEvaluationService>();
builder.Services.AddSingleton<AgentPromptBuilder>();
builder.Services.AddSingleton<AgentCommandParser>();
builder.Services.AddSingleton<IAgentCommandHandler, ShellCommandHandler>();
builder.Services.AddSingleton<AgentCommandInterpreter>();
builder.Services.AddHttpClient<MezhsClient>(client =>
    client.BaseAddress = options.MezhsApi);
builder.Services.AddSingleton<AgentWorker>();
builder.Services.AddHostedService<AgentWorker>(
    services => services.GetRequiredService<AgentWorker>());
builder.Services.AddSingleton<AgentService>();

var app = builder.Build();
app.UseExceptionHandler();

var store = app.Services.GetRequiredService<AgentStore>();
store.Initialize();

app.MapGet("/v1", () => Results.Ok(new
{
    name = "MEŽS Agent",
    version = 1,
    dashboard = "/",
    endpoints = new[]
    {
        "/v1/runtime",
        "/v1/policies",
        "/v1/agent-chats",
        "/v1/executions"
    }
}));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/v1/runtime", async (
    MezhsClient mezhs,
    CancellationToken cancellationToken) =>
{
    var mezhsApiHealthy = await mezhs.IsHealthyAsync(cancellationToken);
    return Results.Ok(new
    {
        status = "ok",
        mezhsApi = options.MezhsApi.ToString(),
        mezhsApiHealthy
    });
});

app.MapGet("/v1/policies", (PolicyRegistry policies) =>
    Results.Ok(policies.GetViews()));

app.MapGet("/v1/policies/{policyId}", (
    string policyId,
    PolicyRegistry policies) =>
    Results.Ok(policies.GetView(policyId)));

app.MapGet("/v1/agent-chats", async (
    AgentStore agentStore,
    MezhsClient mezhs,
    CancellationToken cancellationToken) =>
{
    var views = await Task.WhenAll(agentStore.GetAgentChats()
        .Select(record => ToViewAsync(record, mezhs, cancellationToken)));
    return Results.Ok(views);
});

app.MapGet("/v1/agent-chats/{chatId}", async (
    string chatId,
    AgentStore agentStore,
    MezhsClient mezhs,
    CancellationToken cancellationToken) =>
{
    var chat = agentStore.GetAgentChat(chatId);
    return chat is null
        ? Results.NotFound(new { error = $"Agent chat '{chatId}' was not found." })
        : Results.Ok(await ToViewAsync(chat, mezhs, cancellationToken));
});

app.MapPatch("/v1/agent-chats/{chatId}", async (
    string chatId,
    UpdateAgentChatRequest request,
    AgentService agents,
    MezhsClient mezhs,
    CancellationToken cancellationToken) =>
{
    var chat = agents.SetPaused(chatId, request.Paused);
    return Results.Ok(await ToViewAsync(chat, mezhs, cancellationToken));
});

app.MapGet("/v1/agent-chats/{chatId}/messages", async (
    string chatId,
    AgentStore agentStore,
    MezhsClient mezhs,
    CancellationToken cancellationToken) =>
{
    if (agentStore.GetAgentChat(chatId) is null)
        return Results.NotFound(new { error = $"Agent chat '{chatId}' was not found." });
    return Results.Ok(await mezhs.GetMessagesAsync(chatId, cancellationToken));
});

app.MapGet("/v1/agent-chats/{chatId}/executions", (
    string chatId,
    AgentStore agentStore) =>
{
    if (agentStore.GetAgentChat(chatId) is null)
        return Results.NotFound(new { error = $"Agent chat '{chatId}' was not found." });
    return Results.Ok(agentStore.GetExecutions(chatId));
});

app.MapPost("/v1/executions", (
    CreateExecutionRequest request,
    AgentService agents) =>
{
    var execution = agents.Start(request);
    return Results.Accepted($"/v1/executions/{execution.ExecutionId}", execution);
});

app.MapGet("/v1/executions", (
    string? chatId,
    AgentStore agentStore) =>
    Results.Ok(agentStore.GetExecutions(chatId)));

app.MapGet("/v1/executions/{executionId}", (
    string executionId,
    AgentStore agentStore) =>
{
    var execution = agentStore.GetExecution(executionId);
    return execution is null
        ? Results.NotFound(new { error = $"Execution '{executionId}' was not found." })
        : Results.Ok(execution);
});

app.MapPost("/v1/executions/{executionId}/cancel", (
    string executionId,
    AgentWorker worker) =>
    Results.Ok(worker.Cancel(executionId)));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

Console.WriteLine($"MEŽS Agent config: {configPath}");
Console.WriteLine($"MEŽS Agent listening: {options.Listen}");
Console.WriteLine($"MEŽS API: {options.MezhsApi}");
await app.RunAsync();

static async Task<AgentChatView> ToViewAsync(
    AgentChatRecord record,
    MezhsClient mezhs,
    CancellationToken cancellationToken)
{
    var chat = await mezhs.TryGetChatAsync(record.ChatId, cancellationToken);
    return new AgentChatView(
        record.ChatId,
        record.PolicyId,
        record.OriginSource,
        record.OriginReference,
        record.Paused,
        chat?.Title,
        chat?.ConnectionId,
        record.CreatedAt,
        record.UpdatedAt);
}

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

static string FindAgentWebRoot()
{
    var outputCandidate = Path.Combine(AppContext.BaseDirectory, "agent-web");
    if (File.Exists(Path.Combine(outputCandidate, "index.html")))
        return outputCandidate;

    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Mezhs.sln")))
        {
            var repositoryCandidate = Path.Combine(
                directory.FullName,
                "src",
                "Mezhs.Agent.Web",
                "dist");
            if (File.Exists(Path.Combine(repositoryCandidate, "index.html")))
                return repositoryCandidate;
        }
        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException(
        "The Mezhs.Agent.Web frontend build was not found. Build the project before starting MEŽS Agent.");
}
