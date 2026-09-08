using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Mezhs.Agent;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Policy;
using Mezhs.Agent.Services;
using Mezhs.Api.Client;
using Mezhs.Executor;

var configPath = FindConfigPath(GetOption(args, "--config"));
var options = AgentConfigLoader.Load(configPath);
var executorStorage = Path.Combine(
    Path.GetDirectoryName(options.Storage) ?? Environment.CurrentDirectory,
    "executor.sqlite");
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(options.Listen.ToString());
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new ExecutorService(executorStorage));
builder.Services.AddSingleton<AgentStore>();
builder.Services.AddSingleton<PolicyRegistry>();
builder.Services.AddSingleton<PolicyEvaluationService>();
builder.Services.AddSingleton<AgentPromptBuilder>();
builder.Services.AddSingleton<Parser>();
builder.Services.AddSingleton<Shell>();
builder.Services.AddSingleton<Interpreter>();
builder.Services.AddHttpClient<MezhsApiClient>(client =>
    client.BaseAddress = options.MezhsApi);
builder.Services.AddSingleton<AgentDebugLogBuilder>();
builder.Services.AddSingleton<AgentWorker>();
builder.Services.AddHostedService<AgentWorker>(
    services => services.GetRequiredService<AgentWorker>());
builder.Services.AddSingleton<AgentService>();

var app = builder.Build();
app.UseExceptionHandler();

var store = app.Services.GetRequiredService<AgentStore>();
store.Initialize();

app.MapGet("/", () => Results.Ok(new
{
    name = "MEŽS Agent",
    version = 1,
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
    MezhsApiClient mezhs,
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
    MezhsApiClient mezhs,
    CancellationToken cancellationToken) =>
{
    var views = await Task.WhenAll(agentStore.GetAgentChats()
        .Select(record => ToViewAsync(record, agentStore, mezhs, cancellationToken)));
    return Results.Ok(views);
});

app.MapGet("/v1/agent-chats/{chatId}", async (
    string chatId,
    AgentStore agentStore,
    MezhsApiClient mezhs,
    CancellationToken cancellationToken) =>
{
    var chat = agentStore.GetAgentChat(chatId);
    return chat is null
        ? Results.NotFound(new { error = $"Agent chat '{chatId}' was not found." })
        : Results.Ok(await ToViewAsync(chat, agentStore, mezhs, cancellationToken));
});

app.MapPatch("/v1/agent-chats/{chatId}", async (
    string chatId,
    UpdateAgentChatRequest request,
    AgentService agents,
    AgentStore agentStore,
    MezhsApiClient mezhs,
    CancellationToken cancellationToken) =>
{
    var chat = agents.SetPaused(chatId, request.Paused);
    return Results.Ok(await ToViewAsync(chat, agentStore, mezhs, cancellationToken));
});

app.MapGet("/v1/agent-chats/{chatId}/messages", async (
    string chatId,
    AgentStore agentStore,
    MezhsApiClient mezhs,
    Parser parser,
    CancellationToken cancellationToken) =>
{
    if (agentStore.GetAgentChat(chatId) is null)
        return Results.NotFound(new { error = $"Agent chat '{chatId}' was not found." });
    var messages = await mezhs.GetMessagesAsync(chatId, cancellationToken);
    return Results.Ok(messages.Select(message => AgentApiMapper.ToView(message, parser)));
});

app.MapGet("/v1/agent-chats/{chatId}/executions", (
    string chatId,
    AgentStore agentStore,
    ExecutorService executor) =>
{
    if (agentStore.GetAgentChat(chatId) is null)
        return Results.NotFound(new { error = $"Agent chat '{chatId}' was not found." });
    return Results.Ok(GetExecutionViews(agentStore, executor, chatId));
});

app.MapGet("/v1/agent-chats/{chatId}/debug-log", async (
    string chatId,
    AgentDebugLogBuilder logs,
    CancellationToken cancellationToken) =>
{
    var content = await logs.BuildAsync(chatId, cancellationToken);
    var fileName = $"mezhs-agent-{SafeFilePart(chatId)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log";
    return Results.File(
        Encoding.UTF8.GetBytes(content),
        "text/plain; charset=utf-8",
        fileName);
});

app.MapPost("/v1/executions", (
    CreateExecutionRequest request,
    AgentService agents) =>
{
    var execution = agents.Start(request);
    return Results.Accepted(
        $"/v1/executions/{execution.ExecutionId}",
        AgentApiMapper.ToView(execution));
});

app.MapGet("/v1/executions", (
    string? chatId,
    AgentStore agentStore,
    ExecutorService executor) =>
    Results.Ok(GetExecutionViews(agentStore, executor, chatId)));

app.MapGet("/v1/executions/{executionId}", (
    string executionId,
    AgentStore agentStore,
    ExecutorService executor) =>
{
    if (int.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out var executorId))
    {
        try
        {
            return Results.Ok(AgentApiMapper.ToView(executor.Get(executorId)));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = $"Execution '{executionId}' was not found." });
        }
    }

    var execution = agentStore.GetExecution(executionId);
    return execution is null || execution.Kind != AgentExecutionKind.Agent
        ? Results.NotFound(new { error = $"Execution '{executionId}' was not found." })
        : Results.Ok(AgentApiMapper.ToView(execution));
});

app.MapPost("/v1/executions/{executionId}/cancel", (
    string executionId,
    AgentWorker worker) =>
    Results.Ok(AgentApiMapper.ToView(worker.Cancel(executionId))));

app.MapPost("/v1/executions/{executionId}/kill", (
    string executionId,
    ExecutorService executor) =>
{
    if (!int.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        return Results.BadRequest(new { error = "Only shell Executor executions can be killed through this endpoint." });
    try
    {
        return Results.Ok(AgentApiMapper.ToView(executor.Kill(id)));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Execution '{executionId}' was not found." });
    }
});

app.MapPost("/v1/executions/{executionId}/restart", (
    string executionId,
    ExecutorService executor) =>
{
    if (!int.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        return Results.BadRequest(new { error = "Only shell Executor executions can be restarted through this endpoint." });
    try
    {
        var replacementId = executor.Restart(id);
        return Results.Accepted(
            $"/v1/executions/{replacementId}",
            AgentApiMapper.ToView(executor.Get(replacementId)));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Execution '{executionId}' was not found." });
    }
});

Console.WriteLine($"MEŽS Agent config: {configPath}");
Console.WriteLine($"MEŽS Agent listening: {options.Listen}");
Console.WriteLine($"MEŽS API: {options.MezhsApi}");
Console.WriteLine($"MEŽS Agent workspace: {options.Workspace}");
Console.WriteLine($"MEŽS Executor storage: {executorStorage}");
await app.RunAsync();

static IReadOnlyList<AgentExecutionView> GetExecutionViews(
    AgentStore store,
    ExecutorService executor,
    string? chatId)
{
    var agents = store.GetExecutions(chatId)
        .Where(execution => execution.Kind == AgentExecutionKind.Agent)
        .Select(AgentApiMapper.ToView);
    var shells = executor.List(chatId)
        .Select(AgentApiMapper.ToView);
    return agents.Concat(shells)
        .OrderByDescending(execution => execution.CreatedAt)
        .ThenByDescending(execution => execution.ExecutionId, StringComparer.Ordinal)
        .ToArray();
}

static async Task<AgentChatView> ToViewAsync(
    AgentChatRecord record,
    AgentStore store,
    MezhsApiClient mezhs,
    CancellationToken cancellationToken)
{
    var chat = await mezhs.TryGetChatAsync(record.ChatId, cancellationToken);
    var firstTask = store.GetExecutions(record.ChatId)
        .Where(execution => execution.Kind == AgentExecutionKind.Agent && execution.ParentExecutionId is null)
        .OrderBy(execution => execution.CreatedAt)
        .ThenBy(execution => execution.ExecutionId, StringComparer.Ordinal)
        .FirstOrDefault()?.Request;
    return new AgentChatView(
        record.ChatId,
        record.PolicyId,
        record.OriginSource,
        record.OriginReference,
        record.Paused,
        string.IsNullOrWhiteSpace(firstTask) ? chat?.Title : firstTask,
        chat?.ConnectionId,
        record.CreatedAt,
        record.UpdatedAt);
}

static string SafeFilePart(string value)
{
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    return string.IsNullOrWhiteSpace(result) ? "chat" : result;
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

    var currentCandidate = Path.GetFullPath("agent.yaml");
    if (File.Exists(currentCandidate))
        return currentCandidate;

    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Mezhs.sln")))
        {
            var repositoryCandidate = Path.Combine(directory.FullName, "agent.yaml");
            if (File.Exists(repositoryCandidate))
                return repositoryCandidate;
        }
        directory = directory.Parent;
    }

    var outputCandidate = Path.Combine(AppContext.BaseDirectory, "agent.yaml");
    if (File.Exists(outputCandidate))
        return outputCandidate;

    return currentCandidate;
}
