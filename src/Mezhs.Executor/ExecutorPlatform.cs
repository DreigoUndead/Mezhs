using System.Diagnostics;
using System.Text;

namespace Mezhs.Executor;

internal interface IExecutorPlatform
{
    string ShellFileName { get; }
    IReadOnlyList<string> ShellArguments { get; }
    Encoding? ShellEncoding { get; }
    string CreateShellPayload(string command);
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
    private static readonly string[] Arguments = ["/D", "/Q", "/K", "@chcp 65001>nul"];

    public string ShellFileName => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
    public IReadOnlyList<string> ShellArguments => Arguments;
    public Encoding ShellEncoding => Encoding.UTF8;

    public string CreateShellPayload(string command) => command + "\r\n";

    public void ConfigureRuntimeStartInfo(ProcessStartInfo startInfo) =>
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
}

internal sealed class UnixExecutorPlatform : IExecutorPlatform
{
    public string ShellFileName => "/bin/sh";
    public IReadOnlyList<string> ShellArguments => [];
    public Encoding? ShellEncoding => null;

    public string CreateShellPayload(string command) => command + "\n";

    public void ConfigureRuntimeStartInfo(ProcessStartInfo startInfo)
    {
    }
}