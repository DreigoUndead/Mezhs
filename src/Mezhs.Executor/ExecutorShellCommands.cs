using System.Globalization;
using System.Text.RegularExpressions;

namespace Mezhs.Executor;

// Policy supplies defaults; Executor alone interprets all SH attribute values.
public sealed record ExecutorShellDefaults(
    int CommandTimeoutSeconds = 60,
    int? Limit = null,
    int? LimitStart = null,
    int? LimitEnd = null,
    string? Regex = null);

public sealed record ExecutorShellResult(
    string? ExecutionId,
    bool Succeeded,
    int? ExitCode,
    string? Output,
    string? Error,
    string? RecoveryCommand = null,
    string? RecoveryNote = null);

public sealed record ExecutorShellOptions(
    int TimeoutSeconds, int Limit, int LimitStart, int LimitEnd, string? Regex)
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    public static ExecutorShellOptions Resolve(
        IReadOnlyDictionary<string, string>? attributes,
        ExecutorShellDefaults? defaults = null)
    {
        defaults ??= new ExecutorShellDefaults();
        if (attributes is not null)
            foreach (var name in attributes.Keys)
                if (name is not ("timeout" or "limit" or "limitStart" or "limitEnd" or "regex"))
                    throw new ArgumentException($"Unsupported SH parameter: {name}.");

        var timeout = ReadNumber("timeout", defaults.CommandTimeoutSeconds, positive: true);
        var limit = ReadNumber("limit", defaults.Limit ?? 0);
        var start = ReadNumber("limitStart", defaults.LimitStart ?? 10);
        var end = ReadNumber("limitEnd", defaults.LimitEnd ?? 10);
        var explicitStart = attributes?.ContainsKey("limitStart") == true || defaults.LimitStart.HasValue;
        var explicitEnd = attributes?.ContainsKey("limitEnd") == true || defaults.LimitEnd.HasValue;
        if (limit > 0)
        {
            if (explicitStart && start > limit || explicitEnd && end > limit ||
                explicitStart && explicitEnd && (long)start + end > limit)
                throw new ArgumentException(
                    "Explicit SH limitStart/limitEnd values exceed the effective SH limit.");
            if (explicitStart && !explicitEnd)
                end = Math.Min(end, limit - start);
            else if (explicitEnd && !explicitStart)
                start = Math.Min(start, limit - end);
            else if (!explicitStart && !explicitEnd)
            {
                start = Math.Min(start, limit / 2 + limit % 2);
                end = Math.Min(end, limit - start);
            }
        }

        var regex = defaults.Regex;
        if (attributes?.TryGetValue("regex", out var rawRegex) == true)
        {
            // Require a string token, so bare values and expressions cannot
            // silently become regex patterns. An empty quoted string clears a policy regex.
            if (rawRegex.Length < 2 || rawRegex[0] is not ('"' or '\'') ||
                rawRegex[^1] != rawRegex[0])
                throw new ArgumentException("SH regex must be a quoted string.");
            regex = rawRegex[1..^1];
        }
        if (regex is not null)
        {
            try { _ = new Regex(regex, RegexOptions.CultureInvariant, RegexMatchTimeout); }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid SH regex: {ex.Message}", ex);
            }
        }
        return new ExecutorShellOptions(timeout, limit, start, end, regex);

        int ReadNumber(string name, int policyValue, bool positive = false)
        {
            var value = policyValue;
            if (attributes?.TryGetValue(name, out var raw) == true)
            {
                if (raw.Length >= 2 && (raw[0] is '"' or '\'') && raw[^1] == raw[0])
                    raw = raw[1..^1];
                else if (raw.Length >= 2 && raw[0] == '{' && raw[^1] == '}')
                    raw = raw[1..^1].Trim();
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                    throw new ArgumentException(
                        $"SH {name} must be a {(positive ? "positive" : "non-negative")} integer.");
            }
            if (value < (positive ? 1 : 0))
                throw new ArgumentException(
                    $"SH {name} must be a {(positive ? "positive" : "non-negative")} integer.");
            return value;
        }
    }

    public ExecutorShellResult Present(Execution execution, string storagePath)
    {
        var raw = execution.Result;
        var output = raw;
        var abbreviated = false;
        if (!string.IsNullOrEmpty(raw) && (Limit > 0 || Regex is not null))
        {
            var lines = new List<string>();
            using (var reader = new StringReader(raw))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                    lines.Add(line);
            }
            var selected = lines;
            if (Regex is { } pattern)
            {
                try
                {
                    var matcher = new Regex(pattern, RegexOptions.CultureInvariant, RegexMatchTimeout);
                    selected = lines.Where(line => matcher.IsMatch(line)).ToList();
                }
                catch (RegexMatchTimeoutException)
                {
                    return WithRecovery(
                        "[SH regex evaluation timed out; full output remains in the durable Executor record.]");
                }
                abbreviated = selected.Count != lines.Count;
            }

            if (Limit > 0 && selected.Count > Limit)
            {
                var first = selected.Take(LimitStart);
                var last = LimitEnd == 0 ? Enumerable.Empty<string>()
                    : selected.Skip(selected.Count - LimitEnd);
                var shown = first.Concat(last).ToArray();
                output = string.Join("\n", shown) +
                    $"\n[SH line preview: {shown.Length}/{selected.Count} selected lines shown; {selected.Count - shown.Length} omitted.]";
                abbreviated = true;
            }
            else output = string.Join("\n", selected);

            if (Regex is not null)
                output += $"\n[SH regex filter: {selected.Count}/{lines.Count} original lines matched.]";
        }
        return WithRecovery(output, abbreviated);

        ExecutorShellResult WithRecovery(string? preview, bool wasAbbreviated = true)
        {
            var command = RecoveryCommand(execution.Id, storagePath);
            var note = command is null
                ? "Full output is retained in Executor history; a CLI recovery command could not be resolved."
                : "Run recoveryCommand as an SH block to fetch the stored Executor record without rerunning " +
                  "the original shell command. The record's Result is JSON-escaped. " +
                  "limit=0 disables line abbreviation; regex=\"\" overrides policy regex filtering. " +
                  "An independent Agent model-prompt character safeguard may still abbreviate a large retrieved record.";
            return new ExecutorShellResult(
                execution.Id.ToString(CultureInfo.InvariantCulture),
                execution.Status == ExecutionStatus.Completed,
                execution.ExitCode,
                preview,
                execution.Error,
                command,
                wasAbbreviated ? note : null);
        }
    }

    private static string? RecoveryCommand(int id, string storagePath)
    {
        if (id < 1) return null;
        var assembly = typeof(ExecutorApplication).Assembly.Location;
        if (string.IsNullOrWhiteSpace(assembly) || !File.Exists(assembly))
            return null;
        if (OperatingSystem.IsWindows())
            return $"<SH limit=0 regex=\"\">set \"{ExecutorEnvironment.StorageVariable}={storagePath}\" && dotnet \"{assembly}\" Get {id}</SH>";
        static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return $"<SH limit=0 regex=\"\">{ExecutorEnvironment.StorageVariable}={Quote(storagePath)} dotnet {Quote(assembly)} Get {id}</SH>";
    }
}
