$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'src\Mezhs.Api\bin\Release\net10.0'
$moduleDirectory = Join-Path $root 'integrations\Mezhs.Integrations.ChatGpt\browser\modules'
$chatGptNames = @('account-files.js', 'account-messages.js', 'account-models.js',
    'account-results.js', 'account-stream.js', 'account-transport.js', 'entry.js', 'guest-page.js', 'guest.js')

$checks = @(
    @{ Assembly = 'Mezhs.Integrations.ChatGpt.dll'; Prefix = 'Mezhs.Integrations.ChatGpt.BrowserModule.' },
    @{ Assembly = 'Mezhs.Integrations.Gemini.dll'; Resource = 'Mezhs.Integrations.Gemini.BrowserModule'; Marker = 'gemini.google.com' },
    @{ Assembly = 'Mezhs.Integrations.Grok.dll'; Resource = 'Mezhs.Integrations.Grok.BrowserModule'; Marker = 'module.exports' }
)

foreach ($check in $checks) {
    $assemblyPath = Join-Path $output $check.Assembly
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        throw "Integration assembly was not copied to API output: $($check.Assembly)"
    }
    $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
    $resources = @($assembly.GetManifestResourceNames())
    if ($check.Prefix) {
        $expectedResources = @($chatGptNames | ForEach-Object { $check.Prefix + $_ })
        $actualResources = @($resources | Where-Object { $_.StartsWith($check.Prefix) })
        if (@(Compare-Object $expectedResources $actualResources).Count -ne 0) {
            throw "ChatGPT embedded resources do not match the $($chatGptNames.Count) CommonJS source modules: $($actualResources -join ', ')"
        }
    }
    else { $expectedResources = @($check.Resource) }

    foreach ($resource in $expectedResources) {
        if ($resources -notcontains $resource) {
            throw "Missing resource '$resource' from '$($check.Assembly)'."
        }
        $stream = $assembly.GetManifestResourceStream($resource)
        if ($null -eq $stream) { throw "Could not open browser resource '$resource'." }
        try {
            $buffer = [IO.MemoryStream]::new()
            try { $stream.CopyTo($buffer); $bytes = $buffer.ToArray() }
            finally { $buffer.Dispose() }
        }
        finally { $stream.Dispose() }

        if ($check.Prefix) {
            $name = $resource.Substring($check.Prefix.Length)
            $expected = [IO.File]::ReadAllBytes((Join-Path $moduleDirectory $name))
            if (-not ([Convert]::ToBase64String($bytes) -eq [Convert]::ToBase64String($expected))) {
                throw "Embedded ChatGPT module '$name' differs from source."
            }
        }
        else {
            $source = [Text.Encoding]::UTF8.GetString($bytes)
            if ($source -notlike "*$($check.Marker)*" -or $source -notmatch 'module\.exports') {
                throw "Browser resource '$resource' does not contain the expected runtime module."
            }
        }
    }
}

Write-Host "PASS: all $($chatGptNames.Count) ChatGPT source modules and single-file Grok/Gemini modules are embedded unchanged."
