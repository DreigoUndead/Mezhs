$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$webPort = 5297
$upstreamPort = 5298
$webBase = "http://127.0.0.1:$webPort"
$upstreamBase = "http://127.0.0.1:$upstreamPort"
$agentWebDll = Join-Path $root "src\Mezhs.Agent.Web\bin\Release\net10.0\Mezhs.Agent.Web.dll"
$stdout = Join-Path ([IO.Path]::GetTempPath()) ("mezhs-agent-web-proxy-" + [Guid]::NewGuid().ToString("N") + ".out.log")
$stderr = Join-Path ([IO.Path]::GetTempPath()) ("mezhs-agent-web-proxy-" + [Guid]::NewGuid().ToString("N") + ".err.log")

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class TruncatedHttpServer
{
    private static TcpListener _listener;
    private static Thread _thread;
    private static volatile bool _running;

    public static void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _running = true;
        _thread = new Thread(Run) { IsBackground = true };
        _thread.Start();
    }

    public static void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        try { _thread?.Join(2000); } catch { }
    }

    private static void Run()
    {
        while (_running)
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                Handle(client);
            }
            catch (SocketException)
            {
                if (_running) throw;
            }
            catch (ObjectDisposedException)
            {
                if (_running) throw;
            }
        }
    }

    private static void Handle(TcpClient client)
    {
        using var stream = client.GetStream();
        var request = ReadHeaders(stream);
        var firstLineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
        var firstLine = firstLineEnd >= 0 ? request.Substring(0, firstLineEnd) : request;

        if (firstLine.Contains(" /health ", StringComparison.Ordinal))
        {
            WriteComplete(stream, "{\"status\":\"ok\"}");
            return;
        }

        if (firstLine.Contains(" /v1/broken ", StringComparison.Ordinal))
        {
            var header = "HTTP/1.1 200 OK\r\n" +
                         "Content-Type: application/json\r\n" +
                         "Content-Length: 128\r\n" +
                         "Connection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(header);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            Thread.Sleep(150);
            return;
        }

        var notFound = Encoding.ASCII.GetBytes(
            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        stream.Write(notFound, 0, notFound.Length);
        stream.Flush();
    }

    private static string ReadHeaders(Stream stream)
    {
        using var buffer = new MemoryStream();
        var state = 0;
        while (buffer.Length < 16384)
        {
            var value = stream.ReadByte();
            if (value < 0) break;
            buffer.WriteByte((byte)value);
            if (state == 0 && value == 13) state = 1;
            else if (state == 1 && value == 10) state = 2;
            else if (state == 2 && value == 13) state = 3;
            else if (state == 3 && value == 10) state = 4;
            else state = value == 13 ? 1 : 0;
            if (state == 4) break;
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static void WriteComplete(Stream stream, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var headerText = string.Format(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {0}\r\nConnection: close\r\n\r\n",
            payload.Length);
        var header = Encoding.ASCII.GetBytes(headerText);
        stream.Write(header, 0, header.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }
}
'@

function Wait-Proxy([Net.Http.HttpClient]$client, [Diagnostics.Process]$process) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        if ($process.HasExited) {
            $errorText = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { "" }
            throw "Agent Web exited before becoming ready. $errorText"
        }
        try {
            $response = $client.GetAsync("$webBase/health").GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -eq 200) { return }
            } finally {
                $response.Dispose()
            }
        } catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Agent Web proxy did not become ready."
        }
        Start-Sleep -Milliseconds 150
    } while ($true)
}

$process = $null
$client = [Net.Http.HttpClient]::new()
try {
    [TruncatedHttpServer]::Start($upstreamPort)
    $arguments = @($agentWebDll, "--urls=$webBase", "--Agent:BaseUrl=$upstreamBase")
    $process = Start-Process dotnet -ArgumentList $arguments -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    Wait-Proxy $client $process

    $response = $client.GetAsync("$webBase/v1/broken").GetAwaiter().GetResult()
    try {
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 503 -or $body -notmatch "response was interrupted") {
            throw "Truncated upstream response was not converted to a controlled 503. HTTP $([int]$response.StatusCode): $body"
        }
    } finally {
        $response.Dispose()
    }

    $health = $client.GetAsync("$webBase/health").GetAwaiter().GetResult()
    try {
        if ([int]$health.StatusCode -ne 200) {
            throw "Agent Web proxy did not remain usable after the upstream body failure."
        }
    } finally {
        $health.Dispose()
    }

    Write-Host "PASS: Agent Web converts a post-header upstream body failure into a controlled 503 and remains available."
}
finally {
    $client.Dispose()
    [TruncatedHttpServer]::Stop()
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        try { $process.WaitForExit(3000) | Out-Null } catch { }
    }
    Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
}
