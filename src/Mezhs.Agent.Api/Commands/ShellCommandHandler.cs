using System.Diagnostics;
using System.Text;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;

namespace Mezhs.Agent.Commands;

public sealed class ShellCommandHandler(AgentStore store) : IAgentCommandHandler
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

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

        using var invocation = CreateInvocation(
            context.ParentExecution,
            child,
            commandText);
        using var process = new Process
        {
            StartInfo = invocation.StartInfo
        };
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandCancellation.CancelAfter(context.Timeout);

        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Host shell process could not be started.");

            using var cancellation = commandCancellation.Token.Register(() => TryKill(process));
            stdoutTask = process.StandardOutput.ReadToEndAsync();
            stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(commandCancellation.Token);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            var result = await CaptureAvailableOutputAsync(stdoutTask, stderrTask);
            var seconds = context.Timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var error = $"Shell command timed out after {seconds} seconds.";
            store.Fail(child.ExecutionId, error);
            return new AgentCommandResult(
                Name,
                child.ExecutionId,
                false,
                null,
                string.IsNullOrEmpty(result) ? null : result,
                error);
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

    private static ShellInvocation CreateInvocation(
        ExecutionRecord parent,
        ExecutionRecord child,
        string commandText)
    {
        if (OperatingSystem.IsWindows())
            return CreateWindowsInvocation(parent, child, commandText);

        var startInfo = CreateBaseStartInfo(parent, child);
        startInfo.FileName = "/bin/sh";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(commandText);
        return new ShellInvocation(startInfo, null);
    }

    private static ShellInvocation CreateWindowsInvocation(
        ExecutionRecord parent,
        ExecutionRecord child,
        string commandText)
    {
        var commandFile = Path.Combine(
            Path.GetTempPath(),
            $"mezhs-shell-{child.ExecutionId}-{Guid.NewGuid():N}.cmd");

        // /C is reliable for a simple batch-file invocation but not for arbitrary
        // multiline agent text. Prepend only the host transport setup, then preserve
        // the agent-provided body byte-for-byte after that prefix in the payload.
        // The first line is ASCII so cmd.exe can switch to UTF-8 before it reads the
        // subsequent Unicode command lines.
        var payload = "@chcp 65001>nul\r\n" + commandText;
        File.WriteAllText(commandFile, payload, Utf8WithoutBom);

        try
        {
            var startInfo = CreateBaseStartInfo(parent, child);
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.ArgumentList.Add("/D");
            startInfo.ArgumentList.Add("/Q");
            startInfo.ArgumentList.Add("/C");
            startInfo.ArgumentList.Add("call");
            startInfo.ArgumentList.Add(commandFile);
            return new ShellInvocation(startInfo, commandFile);
        }
        catch
        {
            TryDelete(commandFile);
            throw;
        }
    }

    private static ProcessStartInfo CreateBaseStartInfo(
        ExecutionRecord parent,
        ExecutionRecord child)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.Environment["MEZHS_EXECUTION_ID"] = child.ExecutionId;
        startInfo.Environment["MEZHS_PARENT_EXECUTION_ID"] = parent.ExecutionId;
        startInfo.Environment["MEZHS_CORRELATION_ID"] = parent.CorrelationId;
        startInfo.Environment["MEZHS_SOURCE"] = parent.Source;
        if (!string.IsNullOrWhiteSpace(parent.ChatId))
            startInfo.Environment["MEZHS_CHAT_ID"] = parent.ChatId;

        return startInfo;
    }

    private static async Task<string> CaptureAvailableOutputAsync(
        Task<string>? stdoutTask,
        Task<string>? stderrTask)
    {
        if (stdoutTask is null || stderrTask is null)
            return string.Empty;

        try
        {
            var stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2));
            return FormatResult(stdout, stderr);
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
    }

    private static string FormatResult(string stdout, string stderr)
    {
        var result = new StringBuilder();
        AppendStream(result, "stdout", stdout);
        AppendStream(result, "stderr", stderr);
        return result.ToString();
    }

    private static void AppendStream(StringBuilder result, string name, string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        if (result.Length > 0)
            result.AppendLine();

        var normalized = value.TrimEnd('\r', '\n');
        var firstBreak = normalized.IndexOfAny(['\r', '\n']);
        if (firstBreak < 0)
        {
            result.Append(name).Append(": ").Append(normalized);
            return;
        }

        result.Append(name).Append(": ").Append(normalized[..firstBreak]);
        var remainderStart = firstBreak;
        while (remainderStart < normalized.Length &&
               normalized[remainderStart] is '\r' or '\n')
            remainderStart++;
        if (remainderStart < normalized.Length)
            result.AppendLine().Append(normalized[remainderStart..]);
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ShellInvocation(ProcessStartInfo startInfo, string? temporaryCommandFile) : IDisposable
    {
        public ProcessStartInfo StartInfo { get; } = startInfo;

        public void Dispose()
        {
            if (!string.IsNullOrWhiteSpace(temporaryCommandFile))
                TryDelete(temporaryCommandFile);
        }
    }
}
