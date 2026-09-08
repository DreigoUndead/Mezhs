using System.Diagnostics;
using System.Reflection;

namespace Mezhs.Executor;

internal static class ExecutorRuntimeLauncher
{
    public static void Launch(int id, string storagePath)
    {
        var executorAssembly = typeof(ExecutorApplication).Assembly;
        var assemblyPath = executorAssembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
            throw new InvalidOperationException("Executor assembly path could not be resolved.");

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath) ?? Environment.CurrentDirectory
        };

        var entryAssembly = Assembly.GetEntryAssembly();
        var processPath = Environment.ProcessPath;
        var isExecutorEntryPoint = entryAssembly == executorAssembly && !string.IsNullOrWhiteSpace(processPath);
        var isDotnetHost = !string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

        if (isExecutorEntryPoint && !isDotnetHost)
        {
            startInfo.FileName = processPath!;
        }
        else
        {
            startInfo.FileName = string.IsNullOrWhiteSpace(processPath) || !isDotnetHost ? "dotnet" : processPath;
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("Run");
        startInfo.ArgumentList.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment[ExecutorEnvironment.StorageVariable] = storagePath;
        startInfo.Environment[ExecutorEnvironment.ExecutionIdVariable] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Executor runtime process could not be started.");
    }
}
