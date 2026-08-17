$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$electron = Join-Path $root 'electron\node_modules\electron\dist\electron.exe'
$tests = Join-Path $PSScriptRoot 'browser-provider-operations.test.js'

if (-not (Test-Path $electron)) {
    throw "Electron is not installed. Run 'npm ci --prefix electron' first."
}

$previous = $env:ELECTRON_RUN_AS_NODE
try {
    $env:ELECTRON_RUN_AS_NODE = '1'
    & $electron --test $tests
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    if ($null -eq $previous) {
        Remove-Item Env:ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue
    }
    else {
        $env:ELECTRON_RUN_AS_NODE = $previous
    }
}
