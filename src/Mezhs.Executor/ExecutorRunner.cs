using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Mezhs.Executor;

internal sealed class ExecutorRunner(ExecutorStore store)
{
    public void Run(int id)
    {
        var claimed = store.Claim(id, Environment.ProcessId);
        if (claimed is null)
            return;

        var execution = claimed.Execution;
        var replacementAllowed = true;
        try
        {
            replacementAllowed = RunOwnedProcess(execution, claimed.EnvironmentJson);
        }
        catch (Exception ex)
        {
            store.Finish(id, ExecutionStatus.Failed, null, null, $"Executor runtime failed: {ex.Message}");
            replacementAllowed = false;
        }
        finally
        {
            LaunchReplacement(id, replacementAllowed);
        }
    }

    private bool RunOwnedProcess(Execution execution, string environmentJson)
    {
        using var process = new Process
        {
            StartInfo = CreateShellStartInfo(execution, environmentJson)
        };
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Host shell process could not be started.");
            store.SetProcessId(execution.Id, process.Id);
            stdoutTask = process.StandardOutput.ReadToEndAsync();
            stderrTask = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(CreatePayload(execution.Command));
            process.StandardInput.Flush();
            process.StandardInput.Close();

            var started = Stopwatch.StartNew();
            var nextHeartbeat = TimeSpan.Zero;
            var killed = false;
            var timedOut = false;
            string? terminationError = null;

            while (!process.WaitForExit(200))
            {
                if (execution.TimeoutSeconds > 0 && started.Elapsed >= TimeSpan.FromSeconds(execution.TimeoutSeconds))
                {
                    timedOut = true;
                    terminationError = Terminate(process);
                    break;
                }

                if (started.Elapsed < nextHeartbeat)
                    continue;
                nextHeartbeat = started.Elapsed + TimeSpan.FromSeconds(1);
                var current = store.HeartbeatAndGet(execution.Id);
                if (current?.Status == ExecutionStatus.KillRequested)
                {
                    killed = true;
                    terminationError = Terminate(process);
                    break;
                }
            }

            if (!process.HasExited)
                process.WaitForExit();
            var streams = Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
            var result = FormatResult(streams[0], streams[1]);

            if (terminationError is not null)
            {
                store.Finish(
                    execution.Id,
                    ExecutionStatus.Failed,
                    null,
                    EmptyToNull(result),
                    terminationError);
                return false;
            }

            if (timedOut)
            {
                store.Finish(
                    execution.Id,
                    ExecutionStatus.TimedOut,
                    null,
                    EmptyToNull(result),
                    $"Shell command timed out after {execution.TimeoutSeconds} seconds.");
                return true;
            }

            if (killed)
            {
                store.Finish(
                    execution.Id,
                    ExecutionStatus.Killed,
                    null,
                    EmptyToNull(result),
                    "Killed by request.");
                return true;
            }

            var exitCode = process.ExitCode;
            store.Finish(
                execution.Id,
                exitCode == 0 ? ExecutionStatus.Completed : ExecutionStatus.Failed,
                exitCode,
                EmptyToNull(result),
                exitCode == 0 ? null : $"Shell exited with code {exitCode}.");
            return true;
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
        }
    }

    private ProcessStartInfo CreateShellStartInfo(Execution execution, string environmentJson)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = execution.Directory
        };

        startInfo.Environment.Clear();
        foreach (var (name, value) in store.DeserializeEnvironment(environmentJson))
            startInfo.Environment[name] = value;
        if (!string.IsNullOrWhiteSpace(execution.ParentExecutionId))
            startInfo.Environment[ExecutorEnvironment.ParentExecutionIdVariable] = execution.ParentExecutionId;
        startInfo.Environment[ExecutorEnvironment.ExecutionIdVariable] = execution.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment[ExecutorEnvironment.StorageVariable] = store.Path;

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/D");
            startInfo.ArgumentList.Add("/Q");
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }
        else
        {
            startInfo.FileName = "/bin/sh";
        }
        return startInfo;
    }

    private static string CreatePayload(string command) =>
        OperatingSystem.IsWindows()
            ? "@chcp 65001>nul\r\n" + command + "\r\n"
            : command + "\n";

    private static string? Terminate(Process process)
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
            if (!process.WaitForExit(5000) && !HasExitedOrNotStarted(process))
                return "Shell process-tree termination was not confirmed within 5 seconds.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            if (HasExitedOrNotStarted(process))
                return null;
            return $"Shell process-tree termination could not be confirmed: {ex.Message}";
        }
        return null;
    }

    private void LaunchReplacement(int oldId, bool allowed)
    {
        var current = store.Get(oldId)?.Execution;
        if (current?.RestartedAsId is not > 0)
            return;

        if (!allowed)
        {
            store.FailCreated(
                current.RestartedAsId.Value,
                "Restart handoff was not started because old process termination could not be confirmed.");
            return;
        }

        try
        {
            ExecutorRuntimeLauncher.Launch(current.RestartedAsId.Value, store.Path);
        }
        catch (Exception ex)
        {
            store.FailCreated(current.RestartedAsId.Value, $"Restart runtime could not be started: {ex.Message}");
        }
    }

    private static bool HasExitedOrNotStarted(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
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

    private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;
}
