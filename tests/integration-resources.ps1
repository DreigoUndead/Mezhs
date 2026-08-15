$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'src\Mezhs.Api\bin\Release\net10.0'

$checks = @(
    @{
        Assembly = 'Mezhs.Integrations.ChatGpt.dll'
        Resource = 'Mezhs.Integrations.ChatGpt.BrowserModule'
        Marker = 'https://chatgpt.com/'
    },
    @{
        Assembly = 'Mezhs.Integrations.Gemini.dll'
        Resource = 'Mezhs.Integrations.Gemini.BrowserModule'
        Marker = 'gemini.google.com'
    }
)

foreach ($check in $checks) {
    $assemblyPath = Join-Path $output $check.Assembly
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        throw "Integration assembly was not copied to API output: $($check.Assembly)"
    }

    $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
    $resources = $assembly.GetManifestResourceNames()
    if ($resources -notcontains $check.Resource) {
        throw "Integration assembly '$($check.Assembly)' does not contain browser resource '$($check.Resource)'."
    }

    $stream = $assembly.GetManifestResourceStream($check.Resource)
    if ($null -eq $stream) { throw "Could not open browser resource '$($check.Resource)'." }
    try {
        $reader = [IO.StreamReader]::new($stream)
        try { $source = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally {
        $stream.Dispose()
    }

    if ($source -notmatch 'module\.exports' -or $source -notmatch 'sendPrompt' -or $source -notlike "*$($check.Marker)*") {
        throw "Browser resource '$($check.Resource)' does not contain the expected runtime module."
    }

    # The embedded .ts source deliberately contains only JavaScript runtime syntax.
    # This protects the direct extract-as-.js mechanism from accidentally acquiring
    # TypeScript-only declarations that Node could not execute.
    if ($source -match '(?m)^\s*(interface|type|declare|enum|namespace)\s+') {
        throw "Browser resource '$($check.Resource)' contains TypeScript-only runtime syntax."
    }
}

Write-Host 'PASS: browser integration modules are embedded, copied with plugin DLLs, and executable as JavaScript source.'
