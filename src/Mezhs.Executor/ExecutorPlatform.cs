using System.Diagnostics;
using System.Text;

namespace Mezhs.Executor;

internal sealed record ExecutorShellInvocation(
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    string? TemporaryFile);

internal interface IExecutorPlatform
{
    string ShellFileName { get; }
    Encoding? ShellEncoding { get; }
    ExecutorShellInvocation PrepareShell(string command);
    void ConfigureRuntimeStartInfo(ProcessStartInfo startInfo);
}

internal static class ExecutorPlatform
{
    public static IExecutorPlatform Current { get; } = OperatingSystem.IsWindows()
        ? new WindowsExecutorPlatform()
        : new UnixExecutorPlatform();
}

internal sealed class WindowsExecutorPlatform : IExecutorPlatform
{
    public string ShellFileName => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
    public Encoding ShellEncoding => Encoding.UTF8;

    public ExecutorShellInvocation PrepareShell(string command)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mezhs-executor-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(
            path,
            "@chcp 65001>nul\r\n" + command + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new ExecutorShellInvocation(["/D", "/Q", "/C", path], null, path);
    }

    public void ConfigureRuntimeStartInfo(ProcessStartInfo startInfo) =>
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
}

internal sealed class UnixExecutorPlatform : IExecutorPlatform
{
    public string ShellFileName => "/bin/sh";
    public Encoding? ShellEncoding => null;

    public ExecutorShellInvocation PrepareShell(string command) =>
        new([], command + "\n", null);

    public void ConfigureRuntimeStartInfo(ProcessStartInfo startInfo)
    {
    }
}