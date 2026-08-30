using System.Diagnostics;
using System.Text;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;

namespace Mezhs.Agent.Commands;

public sealed class ShellCommandHandler(AgentStore store) : IAgentCommandHandler
{
    public string Name => "SH";

    public async Task<AgentCommandResult> ExecuteAsync(
        AgentCommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        var commandText = context.Command.Body ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return new AgentCommandResult(
                Name,
                null,
                false,
                null,
                null,
                "SH command body cannot be empty.");
        }

        var child = store.CreateChildExecution(
            context.ParentExecution,
            AgentExecutionKind.Shell,
            commandText);
        if (!store.TryMarkRunning(child.ExecutionId))
        {
            return new AgentCommandResult(
                Name,
                child.ExecutionId,
                false,
                null,
                null,
                "Shell execution could not enter the running state.");
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(context.ParentExecution, child, commandText)
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Host shell process could not be started.");

            using var cancellation = cancellationToken.Register(() => TryKill(process));
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var result = FormatResult(stdout, stderr);
            var exitCode = process.ExitCode;

            store.CompleteShell(child.ExecutionId, exitCode, result);
            return new AgentCommandResult(
                Name,
                child.ExecutionId,
                exitCode == 0,
                exitCode,
                result,
                exitCode == 0 ? null : $"Shell exited with code {exitCode}.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            store.Cancel(child.ExecutionId);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            store.Fail(child.ExecutionId, ex.Message);
            return new AgentCommandResult(
                Name,
                child.ExecutionId,
                false,
                null,
                null,
                ex.Message);
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        ExecutionRecord parent,
        ExecutionRecord child,
        string commandText)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/D");
            startInfo.ArgumentList.Add("/S");
            startInfo.ArgumentList.Add("/C");
            startInfo.ArgumentList.Add(commandText);
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(commandText);
        }

        startInfo.Environment["MEZHS_EXECUTION_ID"] = child.ExecutionId;
        startInfo.Environment["MEZHS_PARENT_EXECUTION_ID"] = parent.ExecutionId;
        startInfo.Environment["MEZHS_CORRELATION_ID"] = parent.CorrelationId;
        startInfo.Environment["MEZHS_SOURCE"] = parent.Source;
        if (!string.IsNullOrWhiteSpace(parent.ChatId))
            startInfo.Environment["MEZHS_CHAT_ID"] = parent.ChatId;

        return startInfo;
    }

    private static string FormatResult(string stdout, string stderr)
    {
        var result = new StringBuilder();
        if (!string.IsNullOrEmpty(stdout))
        {
            result.AppendLine("stdout:");
            result.Append(stdout.TrimEnd('\r', '\n'));
        }
        if (!string.IsNullOrEmpty(stderr))
        {
            if (result.Length > 0)
                result.AppendLine();
            result.AppendLine("stderr:");
            result.Append(stderr.TrimEnd('\r', '\n'));
        }
        return result.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
