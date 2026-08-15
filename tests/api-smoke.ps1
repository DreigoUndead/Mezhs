$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$projectRoot = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $PSScriptRoot 'mezhs.test.yaml'
$dataPath = Join-Path $PSScriptRoot 'data'
$stdoutPath = Join-Path $PSScriptRoot 'api-smoke.out.log'
$stderrPath = Join-Path $PSScriptRoot 'api-smoke.err.log'

# Some launch environments expose both Path and PATH. Windows PowerShell's
# Start-Process rejects that duplicate, so normalize it before spawning dotnet.
$processPath = $env:Path
Remove-Item Env:PATH -ErrorAction SilentlyContinue
$env:Path = $processPath

if (Test-Path -LiteralPath $dataPath) {
    Remove-Item -LiteralPath $dataPath -Recurse -Force
}
foreach ($path in @($stdoutPath, $stderrPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

$process = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', (Join-Path $projectRoot 'src\Mezhs.Api\Mezhs.Api.csproj'), '-c', 'Release', '--no-build', '--', '--config', $configPath) `
    -WorkingDirectory $projectRoot `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -WindowStyle Hidden `
    -PassThru

try {
    $baseUrl = 'http://127.0.0.1:5198'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        try {
            $health = Invoke-RestMethod -Uri "$baseUrl/health"
            break
        }
        catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 200
        }
    } while ($true)

    if ($health.status -ne 'ok') { throw 'Health endpoint failed.' }

    $connections = Invoke-RestMethod -Uri "$baseUrl/v1/connections"
    $plainConnection = $connections | Where-Object { $_.id -eq 'test' }
    $loginConnection = $connections | Where-Object { $_.id -eq 'test-login' }
    if ($plainConnection.requiresLogin) { throw 'Login capability was advertised without a login module.' }
    if (-not $loginConnection.requiresLogin) { throw 'Login module was not advertised by connection metadata.' }
    if ($plainConnection.integration -ne 'mock' -or $loginConnection.integration -ne 'mock-login') {
        throw 'Connection integration metadata was invalid.'
    }
    if ($null -ne $plainConnection.integrationName) {
        throw 'Connection metadata still exposes redundant integrationName.'
    }
    $login = Invoke-RestMethod -Method Post -Uri "$baseUrl/v1/connections/test-login/login"
    if ($login.status -ne 'ready') { throw 'Login module endpoint did not complete.' }

    $category = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/v1/categories" `
        -ContentType 'application/json' `
        -Body '{"name":"Research"}'
    if (-not $category.categoryId) { throw 'Category creation did not return an identifier.' }

    $created = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/v1/messages" `
        -ContentType 'application/json' `
        -Body (ConvertTo-Json @{
            connectionId = 'test'
            categoryId = $category.categoryId
            content = 'hello'
        })

    if (-not $created.messageId -or -not $created.chatId) {
        throw 'Message creation did not return identifiers.'
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $completed = Invoke-RestMethod -Uri "$baseUrl/v1/messages/$($created.messageId)"
        if ($completed.status -eq 'Completed') { break }
        if ($completed.status -eq 'Failed') { throw $completed.error }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Message polling timed out.' }
        Start-Sleep -Milliseconds 50
    } while ($true)

    if ($completed.reply.content -ne 'Echo: hello') { throw 'Unexpected reply.' }

    # Backward-compatible follow-up: an existing chat may omit connectionId and
    # continues through the connection used by its latest message.
    $implicitFollowUp = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/v1/messages" `
        -ContentType 'application/json' `
        -Body (ConvertTo-Json @{
            chatId = $created.chatId
            content = 'implicit same connection'
        })

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $implicitCompleted = Invoke-RestMethod -Uri "$baseUrl/v1/messages/$($implicitFollowUp.messageId)"
        if ($implicitCompleted.status -eq 'Completed') { break }
        if ($implicitCompleted.status -eq 'Failed') { throw $implicitCompleted.error }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Implicit follow-up polling timed out.' }
        Start-Sleep -Milliseconds 50
    } while ($true)
    if ($implicitCompleted.connectionId -ne 'test' -or $implicitCompleted.reply.connectionId -ne 'test') {
        throw 'Existing chat did not inherit its latest connection when connectionId was omitted.'
    }

    $uploadPath = Join-Path $PSScriptRoot 'attachment-smoke.txt'
    [IO.File]::WriteAllText($uploadPath, 'MEZHS-ATTACHMENT-SMOKE', [Text.Encoding]::UTF8)
    $http = [Net.Http.HttpClient]::new()
    try {
        $multipart = [Net.Http.MultipartFormDataContent]::new()
        $multipart.Add([Net.Http.StringContent]::new('test'), 'connectionId')
        $fileStream = [IO.File]::OpenRead($uploadPath)
        $fileContent = [Net.Http.StreamContent]::new($fileStream)
        $fileContent.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')
        $multipart.Add($fileContent, 'file', 'attachment-smoke.txt')
        $uploadResponse = $http.PostAsync("$baseUrl/v1/files", $multipart).GetAwaiter().GetResult()
        $uploadBody = $uploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $uploadResponse.IsSuccessStatusCode) { throw "File upload failed: $uploadBody" }
        $uploaded = $uploadBody | ConvertFrom-Json
    }
    finally {
        if ($null -ne $multipart) { $multipart.Dispose() }
        if ($null -ne $fileStream) { $fileStream.Dispose() }
        $http.Dispose()
    }

    if (-not $uploaded.fileId -or $uploaded.name -ne 'attachment-smoke.txt') {
        throw 'File upload metadata was invalid.'
    }
    $downloaded = (Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl$($uploaded.contentUrl)").Content
    if ($downloaded -notlike '*MEZHS-ATTACHMENT-SMOKE*') { throw 'Downloaded file content did not match.' }

    $attachmentMessage = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/v1/messages" `
        -ContentType 'application/json' `
        -Body (ConvertTo-Json @{
            connectionId = 'test'
            content = 'read the attachment'
            fileIds = @($uploaded.fileId)
        })

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $attachmentCompleted = Invoke-RestMethod -Uri "$baseUrl/v1/messages/$($attachmentMessage.messageId)"
        if ($attachmentCompleted.status -eq 'Completed') { break }
        if ($attachmentCompleted.status -eq 'Failed') { throw $attachmentCompleted.error }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Attachment message polling timed out.' }
        Start-Sleep -Milliseconds 50
    } while ($true)

    $echoedFile = $attachmentCompleted.reply.files | Select-Object -First 1
    if ($echoedFile.name -ne 'echo-attachment-smoke.txt') { throw 'Integration output file was not imported.' }
    $echoedContent = (Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl$($echoedFile.contentUrl)").Content
    if ($echoedContent -notlike '*MEZHS-ATTACHMENT-SMOKE*') { throw 'Integration output file content did not match.' }

    # Same local chat, different connection, reusing a file uploaded through the first connection.
    $second = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/v1/messages" `
        -ContentType 'application/json' `
        -Body (ConvertTo-Json @{
            connectionId = 'test-login'
            chatId = $created.chatId
            content = 'again through the other connection'
            fileIds = @($uploaded.fileId)
        })

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $secondCompleted = Invoke-RestMethod -Uri "$baseUrl/v1/messages/$($second.messageId)"
        if ($secondCompleted.status -eq 'Completed') { break }
        if ($secondCompleted.status -eq 'Failed') { throw $secondCompleted.error }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Second message polling timed out.' }
        Start-Sleep -Milliseconds 50
    } while ($true)
    if ($secondCompleted.connectionId -ne 'test-login' -or $secondCompleted.reply.connectionId -ne 'test-login') {
        throw 'Existing chat did not switch to the selected second connection.'
    }
    if (($secondCompleted.reply.files | Select-Object -First 1).name -ne 'echo-attachment-smoke.txt') {
        throw 'A local upload could not be reused through another connection.'
    }

    # Switch the same local chat back to the original connection.
    $third = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/v1/messages" `
        -ContentType 'application/json' `
        -Body (ConvertTo-Json @{
            connectionId = 'test'
            chatId = $created.chatId
            content = 'back again'
        })

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $thirdCompleted = Invoke-RestMethod -Uri "$baseUrl/v1/messages/$($third.messageId)"
        if ($thirdCompleted.status -eq 'Completed') { break }
        if ($thirdCompleted.status -eq 'Failed') { throw $thirdCompleted.error }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Third message polling timed out.' }
        Start-Sleep -Milliseconds 50
    } while ($true)
    if ($thirdCompleted.connectionId -ne 'test' -or $thirdCompleted.reply.connectionId -ne 'test') {
        throw 'Existing chat did not switch back to the original connection.'
    }

    $chatList = Invoke-RestMethod -Uri "$baseUrl/v1/chats"
    $listedChat = $chatList | Where-Object { $_.chatId -eq $created.chatId }
    if ($listedChat.categoryId -ne $category.categoryId) {
        throw 'Global chat list did not preserve category assignment.'
    }

    $history = Invoke-RestMethod -Uri "$baseUrl/v1/chats/$($created.chatId)/messages"
    if ($history.Count -ne 8) { throw "Expected 8 logged messages, got $($history.Count)." }
    $userConnections = @($history | Where-Object { $_.role -eq 'user' } | ForEach-Object { $_.connectionId })
    if (($userConnections -join ',') -ne 'test,test,test-login,test') {
        throw "Expected user connections test,test,test-login,test; got $($userConnections -join ',')."
    }

    $replay = Invoke-RestMethod -Method Post -Uri "$baseUrl/v1/messages/$($created.messageId)/replay"
    if ($replay.replayOfMessageId -ne $created.messageId) { throw 'Replay linkage failed.' }
    if ($replay.connectionId -ne 'test') { throw 'Replay did not retain the original request connection.' }

    Invoke-RestMethod `
        -Method Patch `
        -Uri "$baseUrl/v1/chats/$($created.chatId)" `
        -ContentType 'application/json' `
        -Body '{"categoryId":null}' | Out-Null
    Invoke-RestMethod -Method Delete -Uri "$baseUrl/v1/categories/$($category.categoryId)"

    Write-Output "PASS message=$($created.messageId) chat=$($created.chatId) history=$($history.Count) implicit-follow-up=ok multi-connection=ok categories=ok files=ok login=ok"
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    $uploadPath = Join-Path $PSScriptRoot 'attachment-smoke.txt'
    if (Test-Path -LiteralPath $uploadPath) {
        Remove-Item -LiteralPath $uploadPath -Force
    }
}
