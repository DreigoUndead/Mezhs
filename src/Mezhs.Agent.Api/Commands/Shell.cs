using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;

namespace Mezhs.Agent.Commands;

public sealed class ShellTerminationException(string message) : Exception(message);

public sealed class Shell(
    AgentStore store,
    AgentOptions options)
{
    public async Task<Result> ExecuteAsync(
        ExecutionContext context,
        string commandText,
        CancellationToken cancellationToken)
    {
        var definition = Registry.Get(CommandBehavior.Shell);
        if (string.IsNullOrWhiteSpace(commandText))
            return new Result(definition.Name, null, false, null, null, "Shell command body cannot be empty.");

        var child = store.CreateChildExecution(
            context.ParentExecution,
            AgentExecutionKind.Shell,
            definition.Name,
            commandText,
            context.TriggerMessageId,
            context.CommandIndex);
        if (!store.TryMarkRunning(child.ExecutionId))
            return new Result(definition.Name, child.ExecutionId, false, null, null, "Shell execution could not enter the running state.");

        var invocation = CreateInvocation(context.ParentExecution, child, commandText);
        using var process = new Process { StartInfo = invocation.StartInfo };
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandCancellation.CancelAfter(context.Timeout);

        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Host shell process could not be started.");

            stdoutTask = process.StandardOutput.ReadToEndAsync();
            stderrTask = process.StandardError.ReadToEndAsync();
            await WriteInputAsync(process, invocation.Payload, commandCancellation.Token);
            await process.WaitForExitAsync(commandCancellation.Token);
            var streams = await Task.WhenAll(stdoutTask, stderrTask);
            var result = FormatResult(streams[0], streams[1]);
            var exitCode = process.ExitCode;

            cancellationToken.ThrowIfCancellationRequested();
            if (!store.CompleteShell(child.ExecutionId, exitCode, result))
            {
                if (store.GetExecution(child.ExecutionId)?.Status == AgentExecutionStatus.CancelRequested)
                {
                    store.CompleteCancellation(child.ExecutionId);
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new InvalidOperationException("Shell execution changed state before its result could be recorded.");
            }

            return new Result(
                definition.Name,
                child.ExecutionId,
                exitCode == 0,
                exitCode,
                result,
                exitCode == 0 ? null : $"Shell exited with code {exitCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var terminationError = await TerminateAsync(process);
            var result = await CaptureAvailableOutputAsync(stdoutTask, stderrTask);
            var seconds = context.Timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var error = $"Shell command timed out after {seconds} seconds.";
            if (terminationError is not null)
                error = $"{error} {terminationError}";
            PersistFailure(child.ExecutionId, error, result);
            if (terminationError is not null)
                throw new ShellTerminationException(error);
            return new Result(definition.Name, child.ExecutionId, false, null, EmptyToNull(result), error);
        }
        catch (OperationCanceledException)
        {
            var terminationError = await TerminateAsync(process);
            var result = await CaptureAvailableOutputAsync(stdoutTask, stderrTask);
            if (terminationError is not null)
            {
                var error = $"Shell cancellation was requested, but termination could not be confirmed. {terminationError}";
                PersistFailure(child.ExecutionId, error, result);
                throw new ShellTerminationException(error);
            }

            store.RequestCancel(child.ExecutionId);
            store.CompleteCancellation(child.ExecutionId);
            throw;
        }
        catch (Exception ex) when (ex is not ShellTerminationException)
        {
            var terminationError = await TerminateAsync(process);
            var result = await CaptureAvailableOutputAsync(stdoutTask, stderrTask);
            var error = terminationError is null ? ex.Message : $"{ex.Message} {terminationError}";
            PersistFailure(child.ExecutionId, error, result);
            if (terminationError is not null)
                throw new ShellTerminationException(error);
            return new Result(definition.Name, child.ExecutionId, false, null, EmptyToNull(result), error);
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
        }
    }

    private void PersistFailure(string executionId, string error, string result)
    {
        if (!store.FailShell(executionId, error, EmptyToNull(result)))
            throw new InvalidOperationException("Shell execution changed state before its failure could be recorded.");
    }

    private ShellInvocation CreateInvocation(
        ExecutionRecord parent,
        ExecutionRecord child,
        string commandText)
    {
        var startInfo = CreateBaseStartInfo(parent, child);
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/D");
            startInfo.ArgumentList.Add("/Q");
            return new ShellInvocation(startInfo, "@chcp 65001>nul\r\n" + commandText + "\r\n");
        }

        startInfo.FileName = "/bin/sh";
        return new ShellInvocation(startInfo, commandText + "\n");
    }

    private ProcessStartInfo CreateBaseStartInfo(
        ExecutionRecord parent,
        ExecutionRecord child)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = options.Workspace
        };

        foreach (var (name, value) in parent.Environment)
            startInfo.Environment[name] = value;
        startInfo.Environment["MEZHS_EXECUTION_ID"] = child.ExecutionId;
        startInfo.Environment["MEZHS_PARENT_EXECUTION_ID"] = parent.ExecutionId;
        startInfo.Environment["MEZHS_CORRELATION_ID"] = parent.CorrelationId;
        startInfo.Environment["MEZHS_SOURCE"] = parent.Source;
        startInfo.Environment["MEZHS_WORKSPACE"] = options.Workspace;
        if (!string.IsNullOrWhiteSpace(parent.ChatId))
            startInfo.Environment["MEZHS_CHAT_ID"] = parent.ChatId;

        if (OperatingSystem.IsWindows())
        {
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }
        return startInfo;
    }

    private static async Task WriteInputAsync(
        Process process,
        string payload,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
    }

    private static async Task<string?> TerminateAsync(Process process)
    {
        if (HasExitedOrNotStarted(process))
            return null;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            if (HasExitedOrNotStarted(process))
                return null;
            return $"Shell process-tree termination failed: {ex.Message}";
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            if (HasExitedOrNotStarted(process))
                return null;
            return ex is TimeoutException
                ? "Shell process-tree termination was not confirmed within 5 seconds."
                : $"Shell process-tree termination could not be confirmed: {ex.Message}";
        }

        return HasExitedOrNotStarted(process)
            ? null
            : "Shell process-tree termination could not be confirmed.";
    }

    private static bool HasExitedOrNotStarted(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static async Task<string> CaptureAvailableOutputAsync(
        Task<string>? stdoutTask,
        Task<string>? stderrTask)
    {
        if (stdoutTask is null || stderrTask is null)
            return string.Empty;

        try
        {
            var streams = await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2));
            return FormatResult(streams[0], streams[1]);
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
        while (remainderStart < normalized.Length && normalized[remainderStart] is '\r' or '\n')
            remainderStart++;
        if (remainderStart < normalized.Length)
            result.AppendLine().Append(normalized[remainderStart..]);
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private sealed record ShellInvocation(ProcessStartInfo StartInfo, string Payload);
}
