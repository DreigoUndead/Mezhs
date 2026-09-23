using System.Globalization;
using System.Text;
using Mezhs;
using Mezhs.Agent;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Policy;
using Mezhs.Agent.Services;
using Mezhs.Configuration;
using Mezhs.Executor;
using Mezhs.Services;

var configPath = ConfigPath.Find(args, "agent.yaml");
var options = AgentConfigLoader.Load(configPath);
var executorStorage = Path.Combine(
    Path.GetDirectoryName(options.AgentStorage) ?? Environment.CurrentDirectory,
    "executor.sqlite");

var builder = WebApplication.CreateBuilder(args);
builder.AddMezhsApi(options);
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
    policy.SetIsOriginAllowed(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
        .AllowAnyHeader()
        .AllowAnyMethod()));
builder.Services.AddExceptionHandler<AgentApiExceptionHandler>();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new ExecutorService(executorStorage));
builder.Services.AddSingleton<AgentStore>();
builder.Services.AddSingleton<AgentRecoveryState>();
builder.Services.AddSingleton<PolicyRegistry>();
builder.Services.AddSingleton<PolicyEvaluationService>();
builder.Services.AddSingleton<AgentPromptBuilder>();
builder.Services.AddSingleton<Parser>();
builder.Services.AddSingleton<Shell>();
builder.Services.AddSingleton<Interpreter>();
builder.Services.AddSingleton<AgentDebugLogBuilder>();
builder.Services.AddSingleton<AgentWorker>();
builder.Services.AddHostedService<AgentWorker>(
    services => services.GetRequiredService<AgentWorker>());
builder.Services.AddSingleton<AgentService>();

var app = builder.Build();
app.UseMezhsApi();
app.UseCors();

var store = app.Services.GetRequiredService<AgentStore>();
store.Initialize();
app.Services.GetRequiredService<AgentRecoveryState>().Prepare();

app.MapGet("/", () => Results.Ok(new
{
    name = "MEŽS Agent",
    version = 1,
    endpoints = new[]
    {
        "/v1/connections",
        "/v1/messages",
        "/v1/chats",
        "/v1/categories",
        "/v1/files",
        "/v1/policies",
        "/v1/manual-chat-configs",
        "/v1/agent-chats",
        "/v1/executions"
    }
}));
app.MapMezhsApi();

app.MapGet("/v1/policies", (PolicyRegistry policies) =>
    Results.Ok(policies.GetViews()));

app.MapGet("/v1/policies/{policyId}", (
    string policyId,
    PolicyRegistry policies) =>
    Results.Ok(policies.GetView(policyId)));

app.MapGet("/v1/manual-chat-configs", (
    AgentOptions agentOptions,
    PolicyRegistry policies) =>
    Results.Ok(agentOptions.ManualChats
        .Select(pair =>
        {
            var policy = policies.Get(pair.Value.PolicyId!);
            return new AgentManualChatView(
                pair.Key,
                policy.Id,
                pair.Value.ConnectionId ?? policy.ConnectionId,
                pair.Value.Model);
        })
        .ToArray()));

app.MapGet("/v1/agent-chats", (
    AgentStore agentStore,
    ChatService chats) =>
    Results.Ok(agentStore.GetAgentChats()
        .Select(record => ToView(record, agentStore, chats))
        .ToArray()));

app.MapGet("/v1/agent-chats/{chatId}", (
    string chatId,
    AgentStore agentStore,
    ChatService chats) =>
{
    var chat = agentStore.GetAgentChat(chatId);
    return chat is null
        ? Results.NotFound(new { error = $"Agent chat '{chatId}' was not found." })
        : Results.Ok(ToView(chat, agentStore, chats));
});

app.MapPatch("/v1/agent-chats/{chatId}", (
    string chatId,
    UpdateAgentChatRequest request,
    AgentService agents,
    AgentStore agentStore,
    ChatService chats) =>
{
    var chat = agents.SetPaused(chatId, request.Paused);
    return Results.Ok(ToView(chat, agentStore, chats));
});

app.MapGet("/v1/agent-chats/{chatId}/messages", (
    string chatId,
    AgentStore agentStore,
    ChatService chats,
    Parser parser) =>
{
    if (agentStore.GetAgentChat(chatId) is null)
        return Results.NotFound(new { error = $"Agent chat '{chatId}' was not found." });
    return Results.Ok(chats.GetMessages(chatId)
        .Select(message => AgentApiMapper.ToView(message, parser)));
});

app.MapGet("/v1/agent-chats/{chatId}/executions", (
    string chatId,
    AgentStore agentStore,
    ExecutorService executorService) =>
{
    if (agentStore.GetAgentChat(chatId) is null)
        return Results.NotFound(new { error = $"Agent chat '{chatId}' was not found." });
    return Results.Ok(GetExecutionViews(agentStore, executorService, chatId));
});

app.MapGet("/v1/agent-chats/{chatId}/debug-log", (
    string chatId,
    AgentDebugLogBuilder logs) =>
{
    var content = logs.Build(chatId);
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
    ExecutorService executorService) =>
    Results.Ok(GetExecutionViews(agentStore, executorService, chatId)));

app.MapGet("/v1/executions/{executionId}", (
    string executionId,
    AgentStore agentStore,
    ExecutorService executorService) =>
{
    if (int.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out var executorId))
        return Results.Ok(AgentApiMapper.ToView(executorService.Get(executorId)));

    var execution = agentStore.GetExecution(executionId);
    return execution is null || execution.Kind != AgentExecutionKind.Agent
        ? Results.NotFound(new { error = $"Execution '{executionId}' was not found." })
        : Results.Ok(AgentApiMapper.ToView(execution));
});

app.MapPost("/v1/executions/{executionId}/cancel", (
    string executionId,
    AgentWorker worker) =>
{
    if (int.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        return Results.BadRequest(new { error = "Shell Executor executions use /kill; only root Agent executions use /cancel." });
    return Results.Ok(AgentApiMapper.ToView(worker.Cancel(executionId)));
});

app.MapPost("/v1/executions/{executionId}/kill", (
    string executionId,
    ExecutorService executorService) =>
{
    if (!int.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        return Results.BadRequest(new { error = "Only shell Executor executions can be killed through this endpoint." });
    return Results.Ok(AgentApiMapper.ToView(executorService.Kill(id)));
});

app.MapPost("/v1/executions/{executionId}/restart", (
    string executionId,
    ExecutorService executorService) =>
{
    if (!int.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        return Results.BadRequest(new { error = "Only shell Executor executions can be restarted through this endpoint." });
    var replacementId = executorService.Restart(id);
    return Results.Accepted(
        $"/v1/executions/{replacementId}",
        AgentApiMapper.ToView(executorService.Get(replacementId)));
});

Console.WriteLine($"MEŽS Agent config: {configPath}");
Console.WriteLine($"MEŽS Agent listening: {options.Server.Listen}");
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

static AgentChatView ToView(
    AgentChatRecord record,
    AgentStore store,
    ChatService chats)
{
    var chat = chats.TryGet(record.ChatId);
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
