@echo off
setlocal
chcp 65001 >nul
set "WAVEBYWAVE_DELTA_SCRIPT=%~f0"
if not "%~1"=="" set "WAVEBYWAVE_SERVER_PREFIX=%~1"
if not "%~2"=="" set "WAVEBYWAVE_BUILD_DIR=%~2"
if not "%~3"=="" set "WAVEBYWAVE_RELEASE_ROOT=%~3"
if not "%~4"=="" set "WAVEBYWAVE_UPDATE_URL=%~4"
if not "%~5"=="" set "WAVEBYWAVE_INSTALL_DIR=%~5"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$raw=[IO.File]::ReadAllText($env:WAVEBYWAVE_DELTA_SCRIPT);$marker='#'+'__POWERSHELL_BELOW__';$at=$raw.LastIndexOf($marker);if($at-lt 0){throw 'PowerShell payload not found'};& ([ScriptBlock]::Create($raw.Substring($at+$marker.Length)))"
exit /b %errorlevel%
#__POWERSHELL_BELOW__

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$ProductName = if ($env:WAVEBYWAVE_PRODUCT_NAME) {
    $env:WAVEBYWAVE_PRODUCT_NAME.Trim()
} else {
    'WaveByWave'
}
if ([string]::IsNullOrWhiteSpace($ProductName)) {
    throw 'Product name cannot be empty.'
}
$ExecutableName = if ($env:WAVEBYWAVE_EXECUTABLE_NAME) {
    $env:WAVEBYWAVE_EXECUTABLE_NAME.Trim()
} else {
    "$ProductName.exe"
}
$DataDirectoryName = "${ProductName}_Data"
$BurstDirectoryName = "${ProductName}_BurstDebugInformation_DoNotShip"
$LegacyProductNames = @()
$LocalManifestFileName = '.wavebywave-manifest.json'

$ScriptPath = [System.IO.Path]::GetFullPath($env:WAVEBYWAVE_DELTA_SCRIPT)
$ScriptRoot = Split-Path -Parent $ScriptPath
$BuildRoot = if ($env:WAVEBYWAVE_BUILD_DIR) {
    [System.IO.Path]::GetFullPath($env:WAVEBYWAVE_BUILD_DIR)
} else {
    Join-Path $ScriptRoot 'WaveByWave-Windows'
}
$ReleaseRoot = if ($env:WAVEBYWAVE_RELEASE_ROOT) {
    [System.IO.Path]::GetFullPath($env:WAVEBYWAVE_RELEASE_ROOT)
} else {
    Join-Path $ScriptRoot 'DeltaServer'
}
$WebRoot = Join-Path $ReleaseRoot 'www'
$ChunkRoot = Join-Path $WebRoot 'chunks'
$ManifestPath = Join-Path $WebRoot 'manifest.json'
$PidPath = Join-Path $ReleaseRoot 'server.pid'
$LogPath = Join-Path $ReleaseRoot 'server.log'
$ConfiguredListenPrefix = if ($env:WAVEBYWAVE_SERVER_PREFIX) {
    $env:WAVEBYWAVE_SERVER_PREFIX
} else {
    'http://0.0.0.0:8787/'
}
$ServerPort = 8787
if ($ConfiguredListenPrefix -match ':(\d{1,5})/?$') {
    $ServerPort = [int]$Matches[1]
}
if ($ServerPort -lt 1 -or $ServerPort -gt 65535) {
    throw "Invalid server port: $ServerPort"
}
$DiscoveryPort = if ($env:WAVEBYWAVE_DISCOVERY_PORT) {
    [int]$env:WAVEBYWAVE_DISCOVERY_PORT
} else {
    $ServerPort + 1
}
$ListenPrefix = "http://0.0.0.0:$ServerPort/"
$PublicUrl = if ($env:WAVEBYWAVE_PUBLIC_URL) {
    $env:WAVEBYWAVE_PUBLIC_URL
} else {
    "http://localhost:$ServerPort/"
}
$script:UpdateUrl = if ($env:WAVEBYWAVE_UPDATE_URL) {
    $env:WAVEBYWAVE_UPDATE_URL.TrimEnd('/') + '/'
} else {
    "http://localhost:$ServerPort/"
}
$InstallRoot = if ($env:WAVEBYWAVE_INSTALL_DIR) {
    [System.IO.Path]::GetFullPath($env:WAVEBYWAVE_INSTALL_DIR)
} else {
    Join-Path $ScriptRoot 'WaveByWave-Windows'
}
$LocalManifestPath = Join-Path $InstallRoot $LocalManifestFileName
$ChunkSize = 4MB
$script:OwnsLanServer = $false
$script:LanRole = 'Not selected'
$script:IsConnected = $false

function Write-Title {
    Clear-Host
    Write-Host '============================================================' -ForegroundColor Cyan
    Write-Host "          $ProductName Unified Delta Updater" -ForegroundColor Cyan
    Write-Host '============================================================' -ForegroundColor Cyan
    Write-Host "Build:   $BuildRoot"
    Write-Host "Storage: $WebRoot"
    Write-Host "Server:  $ListenPrefix"
    Write-Host "Updater: $UpdateUrl -> $InstallRoot"
    Write-Host "Role:    $($script:LanRole)"
    Write-Host
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function Write-ConsoleProgress(
    [string]$Label,
    [long]$Completed,
    [long]$Total,
    [string]$Details
) {
    $width = 32
    $ratio = if ($Total -gt 0) {
        [Math]::Max(0.0, [Math]::Min(1.0, $Completed / [double]$Total))
    } else {
        1.0
    }
    $percent = [int][Math]::Floor($ratio * 100)
    $filled = [int][Math]::Floor($ratio * $width)
    $bar = ('#' * $filled) + ('-' * ($width - $filled))
    Write-Host (
        "`r{0,-9} [{1}] {2,3}%  {3} / {4}  {5}        " -f
        $Label,
        $bar,
        $percent,
        (Format-Size $Completed),
        (Format-Size $Total),
        $Details
    ) -NoNewline
}

function Get-Sha256Hex([byte[]]$Bytes, [int]$Count) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        if ($Count -eq $Bytes.Length) {
            $hash = $sha.ComputeHash($Bytes)
        } else {
            $hash = $sha.ComputeHash($Bytes, 0, $Count)
        }
        return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-FileSha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Get-SafeRelativePath([string]$FullPath, [string]$Root) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $fileFull = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $fileFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "File is outside build directory: $fileFull"
    }
    return $fileFull.Substring($rootFull.Length).Replace('\', '/')
}

function Test-IsExcludedPublishPath([string]$RelativePath) {
    # Keep the local updater state out of the release itself.
    if ($RelativePath.Equals(
        $LocalManifestFileName,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        return $true
    }

    foreach ($legacyProduct in $LegacyProductNames) {
        if ($legacyProduct.Equals(
            $ProductName,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            continue
        }

        $legacyRoots = @(
            "$legacyProduct.exe",
            "${legacyProduct}_Data",
            "${legacyProduct}_BurstDebugInformation_DoNotShip"
        )
        foreach ($legacyRoot in $legacyRoots) {
            if ($RelativePath.Equals(
                $legacyRoot,
                [System.StringComparison]::OrdinalIgnoreCase
            ) -or $RelativePath.StartsWith(
                $legacyRoot + '/',
                [System.StringComparison]::OrdinalIgnoreCase
            )) {
                return $true
            }
        }
    }

    return $false
}

function Find-LocalReusePath(
    [string]$RelativePath,
    [string]$CurrentPath
) {
    if (Test-Path -LiteralPath $CurrentPath -PathType Leaf) {
        return $CurrentPath
    }

    foreach ($legacyProduct in $LegacyProductNames) {
        $legacyRelativePath = $null
        if ($RelativePath.Equals(
            $ExecutableName,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            $legacyRelativePath = "$legacyProduct.exe"
        } elseif ($RelativePath.StartsWith(
            $DataDirectoryName + '/',
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            $suffix = $RelativePath.Substring($DataDirectoryName.Length + 1)
            $legacyRelativePath = "${legacyProduct}_Data/$suffix"
        } elseif ($RelativePath.StartsWith(
            $BurstDirectoryName + '/',
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            $suffix = $RelativePath.Substring($BurstDirectoryName.Length + 1)
            $legacyRelativePath =
                "${legacyProduct}_BurstDebugInformation_DoNotShip/$suffix"
        }

        if ($legacyRelativePath) {
            $legacyPath = Resolve-SafeInstallPath $legacyRelativePath $InstallRoot
            if (Test-Path -LiteralPath $legacyPath -PathType Leaf) {
                return $legacyPath
            }
        }
    }

    return $CurrentPath
}

function Publish-Build([bool]$ResetStorage) {
    if (-not (Test-Path -LiteralPath $BuildRoot -PathType Container)) {
        throw "Build directory not found: $BuildRoot"
    }
    if (-not $env:WAVEBYWAVE_ALLOW_TEST_BUILD) {
        $executablePath = Join-Path $BuildRoot $ExecutableName
        $dataPath = Join-Path $BuildRoot $DataDirectoryName
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "$ExecutableName not found in build directory: $BuildRoot"
        }
        if (-not (Test-Path -LiteralPath $dataPath -PathType Container)) {
            throw "$DataDirectoryName not found in build directory: $BuildRoot"
        }
    }

    if ($ResetStorage -and (Test-Path -LiteralPath $WebRoot)) {
        $safeReleaseRoot = [System.IO.Path]::GetFullPath($ReleaseRoot).TrimEnd('\', '/') + '\'
        $safeWebRoot = [System.IO.Path]::GetFullPath($WebRoot)
        if (-not $safeWebRoot.StartsWith($safeReleaseRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe storage path: $safeWebRoot"
        }
        Remove-Item -LiteralPath $WebRoot -Recurse -Force
    }

    [System.IO.Directory]::CreateDirectory($ChunkRoot) | Out-Null
    $allFiles = @(Get-ChildItem -LiteralPath $BuildRoot -File -Recurse |
        Sort-Object FullName)
    $files = @($allFiles | Where-Object {
        $relative = Get-SafeRelativePath $_.FullName $BuildRoot
        -not (Test-IsExcludedPublishPath $relative)
    })
    if ($files.Count -eq 0) {
        throw 'Build directory is empty.'
    }
    $excludedCount = $allFiles.Count - $files.Count
    if ($excludedCount -gt 0) {
        Write-Host (
            "Skipping $excludedCount local manifest/legacy product files."
        ) -ForegroundColor DarkYellow
    }

    [long]$totalBytes = ($files | Measure-Object Length -Sum).Sum
    [long]$processedBytes = 0
    [long]$newChunkBytes = 0
    [int]$newChunkCount = 0
    $manifestFiles = [System.Collections.Generic.List[object]]::new()
    $buffer = New-Object byte[] $ChunkSize
    $watch = [System.Diagnostics.Stopwatch]::StartNew()

    Write-Host "Indexing $($files.Count) files ($(Format-Size $totalBytes))..." -ForegroundColor Cyan

    foreach ($file in $files) {
        $relative = Get-SafeRelativePath $file.FullName $BuildRoot
        $chunks = [System.Collections.Generic.List[object]]::new()
        $stream = [System.IO.File]::OpenRead($file.FullName)
        try {
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $hash = Get-Sha256Hex $buffer $read
                $chunkDirectory = Join-Path $ChunkRoot $hash.Substring(0, 2)
                $chunkPath = Join-Path $chunkDirectory ($hash + '.bin')
                if (-not (Test-Path -LiteralPath $chunkPath -PathType Leaf)) {
                    [System.IO.Directory]::CreateDirectory($chunkDirectory) | Out-Null
                    $temporaryChunk = $chunkPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
                    $chunkStream = [System.IO.File]::Open(
                        $temporaryChunk,
                        [System.IO.FileMode]::CreateNew,
                        [System.IO.FileAccess]::Write,
                        [System.IO.FileShare]::None
                    )
                    try {
                        $chunkStream.Write($buffer, 0, $read)
                    } finally {
                        $chunkStream.Dispose()
                    }
                    Move-Item -LiteralPath $temporaryChunk -Destination $chunkPath -Force
                    $newChunkBytes += $read
                    $newChunkCount++
                }
                $chunks.Add([ordered]@{ hash = $hash; size = $read })
                $processedBytes += $read
                Write-ConsoleProgress `
                    'Deploy' `
                    $processedBytes `
                    $totalBytes `
                    ("New chunks: {0} ({1})" -f
                        $newChunkCount,
                        (Format-Size $newChunkBytes))
            }
        } finally {
            $stream.Dispose()
        }

        $manifestFiles.Add([ordered]@{
            path = $relative
            size = [long]$file.Length
            sha256 = Get-FileSha256 $file.FullName
            chunks = @($chunks)
        })
    }
    Write-Host

    $version = if ($env:WAVEBYWAVE_RELEASE_VERSION) {
        $env:WAVEBYWAVE_RELEASE_VERSION
    } else {
        [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    }
    $manifestBaseUrl = $PublicUrl.TrimEnd('/') + '/'
    if (-not $env:WAVEBYWAVE_PUBLIC_URL) {
        try {
            $lanAddress = Get-RouteAddress ([System.Net.IPAddress]::Parse('192.0.2.1'))
            if (-not [System.Net.IPAddress]::IsLoopback($lanAddress)) {
                $manifestBaseUrl = "http://$($lanAddress):$ServerPort/"
            }
        } catch {
            # Keep localhost as a safe fallback. LAN discovery still returns
            # the address of the correct adapter to remote clients.
        }
    }
    $manifest = [ordered]@{
        format = 1
        product = $ProductName
        executable = $ExecutableName
        version = $version
        createdUtc = [DateTime]::UtcNow.ToString('o')
        chunkSize = $ChunkSize
        baseUrl = $manifestBaseUrl
        files = @($manifestFiles)
    }
    $json = $manifest | ConvertTo-Json -Depth 8
    $temporaryManifest = $ManifestPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [System.IO.File]::WriteAllText($temporaryManifest, $json, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryManifest -Destination $ManifestPath -Force
    $watch.Stop()

    Write-Host
    Write-Host "Published version $version." -ForegroundColor Green
    Write-Host "New data: $(Format-Size $newChunkBytes) in $newChunkCount chunks."
    Write-Host "Manifest: $ManifestPath"
    Write-Host "Elapsed: $([Math]::Round($watch.Elapsed.TotalSeconds, 1)) sec."
}

function Get-ServerProcess {
    if (-not (Test-Path -LiteralPath $PidPath -PathType Leaf)) {
        return $null
    }
    try {
        $state = Get-Content -LiteralPath $PidPath -Raw | ConvertFrom-Json
        $storedPid = [int]$state.pid
        $storedStart = [DateTime]::Parse(
            [string]$state.startTimeUtc,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind
        )
        if ([System.IO.Path]::GetFullPath([string]$state.script) -ne $ScriptPath) {
            throw 'The state file belongs to another script.'
        }
    } catch {
        Remove-Item -LiteralPath $PidPath -Force -ErrorAction SilentlyContinue
        return $null
    }
    $process = Get-Process -Id $storedPid -ErrorAction SilentlyContinue
    if ($null -eq $process -or
        [Math]::Abs(($process.StartTime.ToUniversalTime() - $storedStart.ToUniversalTime()).TotalSeconds) -gt 2) {
        Remove-Item -LiteralPath $PidPath -Force -ErrorAction SilentlyContinue
        return $null
    }
    return $process
}

function Test-ServerOwnerAlive {
    if ($env:WAVEBYWAVE_SERVER_DETACHED -eq '1' -or
        [string]::IsNullOrWhiteSpace($env:WAVEBYWAVE_SERVER_OWNER_PID)) {
        return $true
    }
    $ownerPid = 0
    if (-not [int]::TryParse($env:WAVEBYWAVE_SERVER_OWNER_PID, [ref]$ownerPid)) {
        return $false
    }
    $owner = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
    if ($null -eq $owner) {
        return $false
    }
    try {
        $expectedStart = [DateTime]::Parse(
            $env:WAVEBYWAVE_SERVER_OWNER_START,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind
        )
        return [Math]::Abs(
            ($owner.StartTime.ToUniversalTime() - $expectedStart.ToUniversalTime()).TotalSeconds
        ) -le 2
    } catch {
        return $false
    }
}

function Get-RouteAddress([System.Net.IPAddress]$RemoteAddress) {
    $probe = [System.Net.Sockets.UdpClient]::new()
    try {
        $probe.Connect($RemoteAddress, 9)
        return ([System.Net.IPEndPoint]$probe.Client.LocalEndPoint).Address
    } finally {
        $probe.Dispose()
    }
}

function Send-SimpleHttpResponse(
    [System.Net.Sockets.NetworkStream]$Stream,
    [string]$Method,
    [string]$RequestPath
) {
    $filePath = $null
    $contentType = 'application/octet-stream'
    $cacheControl = 'public, max-age=31536000, immutable'
    if ($RequestPath -eq 'manifest.json') {
        $filePath = $ManifestPath
        $contentType = 'application/json; charset=utf-8'
        $cacheControl = 'no-store'
    } elseif ($RequestPath -match '^chunks/([0-9a-f]{2})/([0-9a-f]{64})\.bin$' -and
              $Matches[1] -eq $Matches[2].Substring(0, 2)) {
        $filePath = Join-Path (Join-Path $ChunkRoot $Matches[1]) ($Matches[2] + '.bin')
    }

    if ($null -eq $filePath -or -not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        $body = [System.Text.Encoding]::UTF8.GetBytes('Not found')
        $header = [System.Text.Encoding]::ASCII.GetBytes(
            "HTTP/1.1 404 Not Found`r`n" +
            "Content-Type: text/plain; charset=utf-8`r`n" +
            "Content-Length: $($body.Length)`r`n" +
            "Connection: close`r`n`r`n"
        )
        $Stream.Write($header, 0, $header.Length)
        if ($Method -ne 'HEAD') {
            $Stream.Write($body, 0, $body.Length)
        }
        return
    }

    $file = Get-Item -LiteralPath $filePath
    $header = [System.Text.Encoding]::ASCII.GetBytes(
        "HTTP/1.1 200 OK`r`n" +
        "Content-Type: $contentType`r`n" +
        "Content-Length: $($file.Length)`r`n" +
        "Cache-Control: $cacheControl`r`n" +
        "Connection: close`r`n`r`n"
    )
    $Stream.Write($header, 0, $header.Length)
    if ($Method -ne 'HEAD') {
        $source = [System.IO.File]::OpenRead($filePath)
        try {
            $source.CopyTo($Stream, 1MB)
        } finally {
            $source.Dispose()
        }
    }
}

function Handle-HttpClient([System.Net.Sockets.TcpClient]$Client) {
    try {
        $Client.ReceiveTimeout = 10000
        $Client.SendTimeout = 30000
        $stream = $Client.GetStream()
        $requestBytes = [System.Collections.Generic.List[byte]]::new()
        $singleByte = New-Object byte[] 1
        while ($requestBytes.Count -lt 16384) {
            $read = $stream.Read($singleByte, 0, 1)
            if ($read -le 0) {
                break
            }
            $requestBytes.Add($singleByte[0])
            $count = $requestBytes.Count
            if ($count -ge 4 -and
                $requestBytes[$count - 4] -eq 13 -and
                $requestBytes[$count - 3] -eq 10 -and
                $requestBytes[$count - 2] -eq 13 -and
                $requestBytes[$count - 1] -eq 10) {
                break
            }
        }
        $requestText = [System.Text.Encoding]::ASCII.GetString($requestBytes.ToArray())
        $firstLine = ($requestText -split "`r`n", 2)[0]
        if ($firstLine -notmatch '^(GET|HEAD)\s+([^\s]+)\s+HTTP/\d\.\d$') {
            throw "Unsupported HTTP request: $firstLine"
        }
        $method = $Matches[1]
        $rawTarget = $Matches[2]
        $requestPath = [System.Uri]::UnescapeDataString(
            ($rawTarget -split '\?', 2)[0]
        ).TrimStart('/')
        Send-SimpleHttpResponse $stream $method $requestPath
        $stream.Flush()
    } finally {
        $Client.Dispose()
    }
}

function Run-HttpServer {
    [System.IO.Directory]::CreateDirectory($ReleaseRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($WebRoot) | Out-Null
    $tcpListener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Any,
        $ServerPort
    )
    $discovery = [System.Net.Sockets.UdpClient]::new($DiscoveryPort)
    $discovery.EnableBroadcast = $true
    try {
        $tcpListener.Start()
        $self = Get-Process -Id $PID
        $state = [ordered]@{
            pid = $PID
            startTimeUtc = $self.StartTime.ToUniversalTime().ToString('o')
            script = $ScriptPath
        } | ConvertTo-Json
        [System.IO.File]::WriteAllText(
            $PidPath,
            $state,
            [System.Text.UTF8Encoding]::new($false)
        )
        Add-Content -LiteralPath $LogPath -Value (
            "$([DateTime]::Now.ToString('s')) START TCP=$ServerPort UDP=$DiscoveryPort PID=$PID"
        )

        while (Test-ServerOwnerAlive) {
            try {
                while ($discovery.Available -gt 0) {
                    $remote = [System.Net.IPEndPoint]::new(
                        [System.Net.IPAddress]::Any,
                        0
                    )
                    $request = $discovery.Receive([ref]$remote)
                    $message = [System.Text.Encoding]::UTF8.GetString($request)
                    if ($message -eq 'WAVEBYWAVE_DELTA_DISCOVER_V2') {
                        $localAddress = Get-RouteAddress $remote.Address
                        $hasManifest = if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
                            1
                        } else {
                            0
                        }
                        $replyText = (
                            "WAVEBYWAVE_DELTA_SERVER_V2|{0}|{1}|{2}" -f
                            $ServerPort,
                            [Environment]::MachineName,
                            $hasManifest
                        )
                        $reply = [System.Text.Encoding]::UTF8.GetBytes($replyText)
                        $discovery.Send($reply, $reply.Length, $remote) | Out-Null
                    }
                }
                if ($tcpListener.Pending()) {
                    Handle-HttpClient ($tcpListener.AcceptTcpClient())
                } else {
                    Start-Sleep -Milliseconds 15
                }
            } catch {
                Add-Content -LiteralPath $LogPath -Value (
                    "$([DateTime]::Now.ToString('s')) ERROR $($_.Exception.Message)"
                )
            }
        }
    } finally {
        $tcpListener.Stop()
        $discovery.Dispose()
        Remove-Item -LiteralPath $PidPath -Force -ErrorAction SilentlyContinue
        Add-Content -LiteralPath $LogPath -Value (
            "$([DateTime]::Now.ToString('s')) STOP PID=$PID"
        )
    }
}

function Start-DeltaServer {
    if (Get-ServerProcess) {
        Write-Host 'Server is already running.' -ForegroundColor Yellow
        return
    }
    $oldAction = $env:WAVEBYWAVE_DELTA_ACTION
    $oldOwnerPid = $env:WAVEBYWAVE_SERVER_OWNER_PID
    $oldOwnerStart = $env:WAVEBYWAVE_SERVER_OWNER_START
    $env:WAVEBYWAVE_DELTA_ACTION = 'server'
    if ($env:WAVEBYWAVE_SERVER_DETACHED -ne '1') {
        $owner = Get-Process -Id $PID
        $env:WAVEBYWAVE_SERVER_OWNER_PID = "$PID"
        $env:WAVEBYWAVE_SERVER_OWNER_START = $owner.StartTime.ToUniversalTime().ToString('o')
    }
    try {
        $process = Start-Process -FilePath $env:ComSpec `
            -ArgumentList @('/d', '/c', ('"' + $ScriptPath + '"')) `
            -WindowStyle Hidden `
            -PassThru
    } finally {
        $env:WAVEBYWAVE_DELTA_ACTION = $oldAction
        $env:WAVEBYWAVE_SERVER_OWNER_PID = $oldOwnerPid
        $env:WAVEBYWAVE_SERVER_OWNER_START = $oldOwnerStart
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        Start-Sleep -Milliseconds 150
        $server = Get-ServerProcess
        if ($server) {
            if ($env:WAVEBYWAVE_SERVER_DETACHED -ne '1') {
                $script:OwnsLanServer = $true
            }
            Write-Host (
                "LAN server started. PID $($server.Id), TCP $ServerPort, discovery UDP $DiscoveryPort"
            ) -ForegroundColor Green
            return
        }
        if ($process.HasExited) { break }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Server did not start. Check log: $LogPath"
}

function Stop-DeltaServer {
    $process = Get-ServerProcess
    if ($null -eq $process) {
        Write-Host 'Server is not running.' -ForegroundColor Yellow
        return
    }
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit()
    Remove-Item -LiteralPath $PidPath -Force -ErrorAction SilentlyContinue
    Write-Host "Server stopped (PID $($process.Id))." -ForegroundColor Green
}

function Show-Status {
    $process = Get-ServerProcess
    if ($process) {
        Write-Host "Server: RUNNING (PID $($process.Id))" -ForegroundColor Green
    } else {
        Write-Host 'Server: STOPPED' -ForegroundColor Yellow
    }
    if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        $size = [long](($manifest.files | Measure-Object size -Sum).Sum)
        Write-Host "Release: $($manifest.version), $(@($manifest.files).Count) files, $(Format-Size $size)"
        Write-Host "URL:     $($manifest.baseUrl)"
    } else {
        Write-Host 'Release: not deployed'
    }
}

function Get-LocalIPv4Addresses {
    $addresses = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    $addresses.Add('127.0.0.1') | Out-Null
    foreach ($networkInterface in
        [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($networkInterface.OperationalStatus -ne
            [System.Net.NetworkInformation.OperationalStatus]::Up) {
            continue
        }
        foreach ($unicast in $networkInterface.GetIPProperties().UnicastAddresses) {
            if ($unicast.Address.AddressFamily -eq
                [System.Net.Sockets.AddressFamily]::InterNetwork) {
                $addresses.Add($unicast.Address.ToString()) | Out-Null
            }
        }
    }
    return ,$addresses
}

function Get-DiscoveryBroadcastAddresses {
    $broadcasts = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    $broadcasts.Add('255.255.255.255') | Out-Null
    foreach ($networkInterface in
        [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($networkInterface.OperationalStatus -ne
            [System.Net.NetworkInformation.OperationalStatus]::Up) {
            continue
        }
        foreach ($unicast in $networkInterface.GetIPProperties().UnicastAddresses) {
            if ($unicast.Address.AddressFamily -ne
                [System.Net.Sockets.AddressFamily]::InterNetwork -or
                $null -eq $unicast.IPv4Mask -or
                [System.Net.IPAddress]::IsLoopback($unicast.Address)) {
                continue
            }
            $addressBytes = $unicast.Address.GetAddressBytes()
            $maskBytes = $unicast.IPv4Mask.GetAddressBytes()
            $broadcastBytes = New-Object byte[] 4
            for ($index = 0; $index -lt 4; $index++) {
                $broadcastBytes[$index] =
                    $addressBytes[$index] -bor (255 -bxor $maskBytes[$index])
            }
            $broadcastAddress =
                [System.Net.IPAddress]::new($broadcastBytes).ToString()
            $broadcasts.Add($broadcastAddress) | Out-Null
        }
    }
    return @($broadcasts)
}

function Find-LanDeltaServer([int]$TimeoutMilliseconds = 1000) {
    $client = [System.Net.Sockets.UdpClient]::new(0)
    $client.EnableBroadcast = $true
    try {
        $localAddresses = Get-LocalIPv4Addresses
        $request = [System.Text.Encoding]::UTF8.GetBytes('WAVEBYWAVE_DELTA_DISCOVER_V2')
        $legacyRequest = [System.Text.Encoding]::UTF8.GetBytes('WAVEBYWAVE_DELTA_DISCOVER_V1')
        foreach ($broadcastAddress in Get-DiscoveryBroadcastAddresses) {
            try {
                $broadcast = [System.Net.IPEndPoint]::new(
                    [System.Net.IPAddress]::Parse($broadcastAddress),
                    $DiscoveryPort
                )
                $client.Send($request, $request.Length, $broadcast) | Out-Null
                $client.Send($legacyRequest, $legacyRequest.Length, $broadcast) | Out-Null
            } catch {
                # One inactive/VPN adapter must not prevent discovery through
                # the remaining active adapters.
            }
        }
        $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($client.Available -gt 0) {
                $remote = [System.Net.IPEndPoint]::new(
                    [System.Net.IPAddress]::Any,
                    0
                )
                $replyBytes = $client.Receive([ref]$remote)
                $reply = [System.Text.Encoding]::UTF8.GetString($replyBytes)
                if ($reply -match '^WAVEBYWAVE_DELTA_SERVER_V2\|(\d{1,5})\|([^|]+)\|([01])$') {
                    $port = [int]$Matches[1]
                    $machineName = $Matches[2]
                    $hasManifest = $Matches[3] -eq '1'
                    $remoteAddress = $remote.Address.ToString()
                    if ($port -ge 1 -and
                        $port -le 65535 -and
                        ($env:WAVEBYWAVE_ALLOW_LOCAL_DISCOVERY -eq '1' -or
                         -not $localAddresses.Contains($remoteAddress))) {
                        return [pscustomobject]@{
                            url = "http://${remoteAddress}:$port/"
                            address = $remoteAddress
                            machine = $machineName
                            hasManifest = $hasManifest
                        }
                    }
                } elseif ($reply -match '^WAVEBYWAVE_DELTA_SERVER_V1\|(https?://.+/)$') {
                    $legacyUri = $null
                    $remoteAddress = $remote.Address.ToString()
                    if (-not $localAddresses.Contains($remoteAddress) -and
                        [System.Uri]::TryCreate(
                            $Matches[1],
                            [System.UriKind]::Absolute,
                            [ref]$legacyUri
                        )) {
                        return [pscustomobject]@{
                            url = "http://${remoteAddress}:$($legacyUri.Port)/"
                            address = $remoteAddress
                            machine = 'Legacy host'
                            hasManifest = $true
                        }
                    }
                }
            } else {
                Start-Sleep -Milliseconds 20
            }
        }
        return $null
    } finally {
        $client.Dispose()
    }
}

function Select-HostRole {
    $leftoverServer = Get-ServerProcess
    if ($leftoverServer -and -not $script:OwnsLanServer) {
        Write-Host (
            "Restarting a leftover server (PID $($leftoverServer.Id)) with the current protocol..."
        ) -ForegroundColor Yellow
        Stop-DeltaServer
        Start-Sleep -Milliseconds 300
    }
    Start-DeltaServer
    Set-UpdateServerUrl "http://127.0.0.1:$ServerPort/"
    $script:LanRole = 'HOST'
    $script:IsConnected = $true
    Write-Host "Hosting on TCP $ServerPort; discovery UDP $DiscoveryPort." -ForegroundColor Green
    Write-Host 'Keep this window open while other computers are connected.'
}

function Connect-ToLanHost {
    if ($script:OwnsLanServer) {
        throw 'This updater is already the host.'
    }
    $leftoverServer = Get-ServerProcess
    if ($leftoverServer) {
        Write-Host (
            "Stopping a leftover local server (PID $($leftoverServer.Id)) before connecting..."
        ) -ForegroundColor Yellow
        Stop-DeltaServer
        Start-Sleep -Milliseconds 300
    }
    Write-Host 'Searching for the host in the local network...'
    $foundHost = Find-LanDeltaServer 2200
    if (-not $foundHost) {
        throw (
            "Host was not found. Start Updater on the host computer, choose HOST, " +
            'and allow it through Windows Firewall for private networks.'
        )
    }
    Set-UpdateServerUrl $foundHost.url
    $script:LanRole = 'CLIENT'
    $script:IsConnected = $true
    Write-Host (
        "Connected to host '$($foundHost.machine)' at $UpdateUrl"
    ) -ForegroundColor Green
    if ($foundHost.hasManifest) {
        try {
            $remoteManifest = Get-RemoteText ($UpdateUrl + 'manifest.json') |
                ConvertFrom-Json
            Write-Host (
                "Remote build is ready: version $($remoteManifest.version), " +
                "$(@($remoteManifest.files).Count) files."
            ) -ForegroundColor Green
        } catch {
            $script:LanRole = 'Not selected'
            $script:IsConnected = $false
            throw "Host was discovered, but its build cannot be read: $($_.Exception.Message)"
        }
    } else {
        Write-Host (
            'The host is connected, but DEPLOY has not been completed there yet.'
        ) -ForegroundColor Yellow
    }
}

function Resolve-SafeInstallPath([string]$RelativePath, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.IndexOf([char]0) -ge 0) {
        throw "Unsafe manifest path: $RelativePath"
    }
    $normalized = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    $result = [System.IO.Path]::GetFullPath((Join-Path $Root $normalized))
    if (-not $result.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest path leaves install directory: $RelativePath"
    }
    return $result
}

function Get-RemoteText([string]$Url) {
    try {
        $request = [System.Net.HttpWebRequest]::CreateHttp($Url)
        $request.Method = 'GET'
        $request.CachePolicy = [System.Net.Cache.RequestCachePolicy]::new(
            [System.Net.Cache.RequestCacheLevel]::NoCacheNoStore
        )
        $response = $request.GetResponse()
        try {
            $reader = [System.IO.StreamReader]::new(
                $response.GetResponseStream(),
                [System.Text.Encoding]::UTF8
            )
            try {
                return $reader.ReadToEnd()
            } finally {
                $reader.Dispose()
            }
        } finally {
            $response.Dispose()
        }
    } catch [System.Net.WebException] {
        if ($_.Exception.Response -and
            [int]$_.Exception.Response.StatusCode -eq 404) {
            throw "The server at $UpdateUrl is reachable, but no build has been deployed there."
        }
        throw
    }
}

function Set-UpdateServerUrl([string]$Url) {
    $candidate = $Url.Trim()
    $parsed = $null
    if (-not [System.Uri]::TryCreate(
            $candidate,
            [System.UriKind]::Absolute,
            [ref]$parsed
        ) -or
        ($parsed.Scheme -ne 'http' -and $parsed.Scheme -ne 'https')) {
        throw "Invalid server URL. Example: http://192.168.1.10:8787/"
    }
    $script:UpdateUrl = $parsed.AbsoluteUri.TrimEnd('/') + '/'
}

function Get-LocalFileChunkIndex([string]$Path, [int]$BlockSize) {
    $index = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $index
    }
    $buffer = New-Object byte[] $BlockSize
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        [long]$offset = 0
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $hash = Get-Sha256Hex $buffer $read
            if (-not $index.ContainsKey($hash)) {
                $index[$hash] = [pscustomobject]@{
                    offset = $offset
                    size = $read
                }
            }
            $offset += $read
        }
    } finally {
        $stream.Dispose()
    }
    return $index
}

function Download-Chunk(
    [string]$Hash,
    [int]$ExpectedSize,
    [string]$CacheRoot
) {
    $cached = Join-Path $CacheRoot ($Hash + '.bin')
    if (Test-Path -LiteralPath $cached -PathType Leaf) {
        return $cached
    }

    $url = $UpdateUrl + 'chunks/' + $Hash.Substring(0, 2) + '/' + $Hash + '.bin'
    $request = [System.Net.HttpWebRequest]::CreateHttp($url)
    $request.Method = 'GET'
    $request.CachePolicy = [System.Net.Cache.RequestCachePolicy]::new(
        [System.Net.Cache.RequestCacheLevel]::NoCacheNoStore
    )
    $response = $request.GetResponse()
    try {
        $source = $response.GetResponseStream()
        $target = [System.IO.File]::Open(
            $cached,
            [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None
        )
        try {
            $buffer = New-Object byte[] 1MB
            [long]$received = 0
            while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $target.Write($buffer, 0, $read)
                $received += $read
            }
        } finally {
            $target.Dispose()
            $source.Dispose()
        }
    } finally {
        $response.Dispose()
    }

    if ($received -ne $ExpectedSize -or (Get-FileSha256 $cached) -ne $Hash) {
        Remove-Item -LiteralPath $cached -Force -ErrorAction SilentlyContinue
        throw "Downloaded chunk verification failed: $Hash"
    }
    return $cached
}

function Update-LocalBuild {
    Set-UpdateServerUrl $UpdateUrl
    Write-Host "Reading manifest from $UpdateUrl..."
    $manifest = Get-RemoteText ($UpdateUrl + 'manifest.json') | ConvertFrom-Json
    if ([int]$manifest.format -ne 1 -or [int]$manifest.chunkSize -le 0) {
        throw 'Unsupported or invalid server manifest.'
    }
    $manifestProduct = [string]$manifest.product
    $isExpectedProduct = $manifestProduct.Equals(
        $ProductName,
        [System.StringComparison]::OrdinalIgnoreCase
    )
    if (-not $isExpectedProduct) {
        foreach ($legacyProduct in $LegacyProductNames) {
            if ($manifestProduct.Equals(
                $legacyProduct,
                [System.StringComparison]::OrdinalIgnoreCase
            )) {
                $isExpectedProduct = $true
                break
            }
        }
    }
    if (-not $isExpectedProduct) {
        throw "Manifest is for '$manifestProduct', expected '$ProductName'."
    }
    $remoteFiles = @($manifest.files)
    if ($remoteFiles.Count -eq 0) {
        throw 'The deployed build is empty.'
    }

    $remotePaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    foreach ($file in $remoteFiles) {
        $resolved = Resolve-SafeInstallPath ([string]$file.path) $InstallRoot
        if (-not $remotePaths.Add($resolved)) {
            throw "Duplicate manifest path: $($file.path)"
        }
        if ([string]$file.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "Invalid file hash: $($file.path)"
        }
        [long]$chunkBytes = 0
        foreach ($chunk in @($file.chunks)) {
            if ([string]$chunk.hash -notmatch '^[0-9a-fA-F]{64}$' -or
                [int]$chunk.size -le 0 -or
                [int]$chunk.size -gt [int]$manifest.chunkSize) {
                throw "Invalid chunk data: $($file.path)"
            }
            $chunkBytes += [int]$chunk.size
        }
        if ($chunkBytes -ne [long]$file.size) {
            throw "Chunk sizes do not match file size: $($file.path)"
        }
    }

    [System.IO.Directory]::CreateDirectory($InstallRoot) | Out-Null
    $changedFiles = [System.Collections.Generic.List[object]]::new()
    [long]$releaseBytes = 0
    foreach ($file in $remoteFiles) {
        $releaseBytes += [long]$file.size
    }
    [long]$checkedBytes = 0
    [int]$checkNumber = 0
    foreach ($file in $remoteFiles) {
        $checkNumber++
        $destination = Resolve-SafeInstallPath ([string]$file.path) $InstallRoot
        Write-ConsoleProgress `
            'Checking' `
            $checkedBytes `
            $releaseBytes `
            ([string]$file.path)
        $matches = $false
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $localItem = Get-Item -LiteralPath $destination
            if ($localItem.Length -eq [long]$file.size) {
                $matches = (Get-FileSha256 $destination) -eq
                    ([string]$file.sha256).ToLowerInvariant()
            }
        }
        if (-not $matches) {
            $changedFiles.Add($file)
        }
        $checkedBytes += [long]$file.size
        Write-ConsoleProgress `
            'Checking' `
            $checkedBytes `
            $releaseBytes `
            ("Files: {0}/{1}" -f
                $checkNumber,
                $remoteFiles.Count)
    }
    Write-Host

    $obsoleteFiles = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $LocalManifestPath -PathType Leaf) {
        try {
            $oldManifest = Get-Content -LiteralPath $LocalManifestPath -Raw |
                ConvertFrom-Json
            foreach ($oldFile in @($oldManifest.files)) {
                $oldPath = Resolve-SafeInstallPath ([string]$oldFile.path) $InstallRoot
                if (-not $remotePaths.Contains($oldPath) -and
                    (Test-Path -LiteralPath $oldPath -PathType Leaf)) {
                    $obsoleteFiles.Add($oldPath)
                }
            }
        } catch {
            Write-Host 'Old manifest is invalid; obsolete files will be preserved.' `
                -ForegroundColor Yellow
        }
    }

    if ($changedFiles.Count -eq 0 -and $obsoleteFiles.Count -eq 0) {
        $manifestJson = $manifest | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText(
            $LocalManifestPath,
            $manifestJson,
            [System.Text.UTF8Encoding]::new($false)
        )
        Write-Host "Version $($manifest.version) is already installed." -ForegroundColor Green
        return
    }

    $parentRoot = Split-Path -Parent $InstallRoot
    $updateRoot = Join-Path $parentRoot ('.wavebywave-update-' + [Guid]::NewGuid().ToString('N'))
    $stageRoot = Join-Path $updateRoot 'files'
    $cacheRoot = Join-Path $updateRoot 'chunks'
    $backupRoot = Join-Path $parentRoot ('.wavebywave-backup-' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($stageRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
    [long]$downloadedBytes = 0
    [long]$reusedBytes = 0
    $downloadedHashes = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    [long]$changedBytes = 0
    foreach ($file in $changedFiles) {
        $changedBytes += [long]$file.size
    }
    [long]$preparedBytes = 0
    $applied = $false

    try {
        [int]$fileNumber = 0
        foreach ($file in $changedFiles) {
            $fileNumber++
            $relative = [string]$file.path
            $currentPath = Resolve-SafeInstallPath $relative $InstallRoot
            $reusePath = Find-LocalReusePath $relative $currentPath
            $stagedPath = Resolve-SafeInstallPath $relative $stageRoot
            Write-ConsoleProgress `
                'Update' `
                $preparedBytes `
                $changedBytes `
                ("Scanning {0}" -f $relative)
            [System.IO.Directory]::CreateDirectory((Split-Path -Parent $stagedPath)) | Out-Null
            $localIndex = Get-LocalFileChunkIndex $reusePath ([int]$manifest.chunkSize)
            $localStream = if (Test-Path -LiteralPath $reusePath -PathType Leaf) {
                [System.IO.File]::OpenRead($reusePath)
            } else {
                $null
            }
            $output = [System.IO.File]::Open(
                $stagedPath,
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None
            )
            try {
                foreach ($chunk in @($file.chunks)) {
                    $hash = ([string]$chunk.hash).ToLowerInvariant()
                    $size = [int]$chunk.size
                    if ($localStream -and $localIndex.ContainsKey($hash)) {
                        $sourceInfo = $localIndex[$hash]
                        $localStream.Position = [long]$sourceInfo.offset
                        $remaining = $size
                        $buffer = New-Object byte[] ([Math]::Min(1MB, $size))
                        while ($remaining -gt 0) {
                            $read = $localStream.Read(
                                $buffer,
                                0,
                                [Math]::Min($buffer.Length, $remaining)
                            )
                            if ($read -le 0) {
                                throw "Local block disappeared while rebuilding $relative."
                            }
                            $output.Write($buffer, 0, $read)
                            $remaining -= $read
                        }
                        $reusedBytes += $size
                    } else {
                        $wasDownloaded = $downloadedHashes.Contains($hash)
                        $cached = Download-Chunk $hash $size $cacheRoot
                        if (-not $wasDownloaded) {
                            $downloadedHashes.Add($hash) | Out-Null
                            $downloadedBytes += $size
                        }
                        $source = [System.IO.File]::OpenRead($cached)
                        try {
                            $source.CopyTo($output, 1MB)
                        } finally {
                            $source.Dispose()
                        }
                    }
                    $preparedBytes += $size
                    Write-ConsoleProgress `
                        'Update' `
                        $preparedBytes `
                        $changedBytes `
                        ("Downloaded {0}; reused {1}; files {2}/{3}" -f
                            (Format-Size $downloadedBytes),
                            (Format-Size $reusedBytes),
                            $fileNumber,
                            $changedFiles.Count)
                }
            } finally {
                $output.Dispose()
                if ($localStream) {
                    $localStream.Dispose()
                }
            }

            if ((Get-FileSha256 $stagedPath) -ne
                ([string]$file.sha256).ToLowerInvariant()) {
                throw "Rebuilt file verification failed: $relative"
            }
        }
        Write-Host

        [System.IO.Directory]::CreateDirectory($backupRoot) | Out-Null
        $movedOld = [System.Collections.Generic.List[object]]::new()
        $installedNew = [System.Collections.Generic.List[string]]::new()
        try {
            foreach ($file in $changedFiles) {
                $relative = [string]$file.path
                $destination = Resolve-SafeInstallPath $relative $InstallRoot
                $staged = Resolve-SafeInstallPath $relative $stageRoot
                if (Test-Path -LiteralPath $destination -PathType Leaf) {
                    $backup = Resolve-SafeInstallPath $relative $backupRoot
                    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $backup)) |
                        Out-Null
                    Move-Item -LiteralPath $destination -Destination $backup
                    $movedOld.Add([pscustomobject]@{
                        original = $destination
                        backup = $backup
                    })
                }
                [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) |
                    Out-Null
                Move-Item -LiteralPath $staged -Destination $destination
                $installedNew.Add($destination)
            }

            $installPrefixLength =
                [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/').Length
            foreach ($obsolete in $obsoleteFiles) {
                $relative = $obsolete.Substring($installPrefixLength).TrimStart('\', '/')
                $backup = Resolve-SafeInstallPath $relative $backupRoot
                [System.IO.Directory]::CreateDirectory((Split-Path -Parent $backup)) |
                    Out-Null
                Move-Item -LiteralPath $obsolete -Destination $backup
                $movedOld.Add([pscustomobject]@{
                    original = $obsolete
                    backup = $backup
                })
            }

            $manifestJson = $manifest | ConvertTo-Json -Depth 8
            $manifestTemp = $LocalManifestPath + '.tmp'
            [System.IO.File]::WriteAllText(
                $manifestTemp,
                $manifestJson,
                [System.Text.UTF8Encoding]::new($false)
            )
            Move-Item -LiteralPath $manifestTemp -Destination $LocalManifestPath -Force
            $applied = $true
        } catch {
            foreach ($newPath in $installedNew) {
                Remove-Item -LiteralPath $newPath -Force -ErrorAction SilentlyContinue
            }
            foreach ($entry in $movedOld) {
                if (Test-Path -LiteralPath $entry.backup -PathType Leaf) {
                    [System.IO.Directory]::CreateDirectory(
                        (Split-Path -Parent $entry.original)
                    ) | Out-Null
                    Move-Item -LiteralPath $entry.backup `
                        -Destination $entry.original `
                        -Force
                }
            }
            throw
        }
    } finally {
        Remove-Item -LiteralPath $updateRoot -Recurse -Force -ErrorAction SilentlyContinue
        if ($applied) {
            Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Updated to version $($manifest.version)." -ForegroundColor Green
    Write-Host "Downloaded: $(Format-Size $downloadedBytes); reused: $(Format-Size $reusedBytes)."
}

function Invoke-Action([string]$Action) {
    switch ($Action.ToLowerInvariant()) {
        'server' { Run-HttpServer; break }
        'start' { Start-DeltaServer; break }
        'host' { Select-HostRole; break }
        'connect' { Connect-ToLanHost; break }
        'deploy' { Publish-Build $false; break }
        'reset' { Publish-Build $true; break }
        'publish' { Publish-Build $false; break }
        'update' { Update-LocalBuild; break }
        'stop' { Stop-DeltaServer; break }
        'status' { Show-Status; break }
        default { throw "Unknown action: $Action" }
    }
}

try {
    if ($env:WAVEBYWAVE_DELTA_ACTION) {
        Invoke-Action $env:WAVEBYWAVE_DELTA_ACTION
        exit 0
    }

    while ($true) {
        Write-Title
        Write-Host '1. HOST'
        Write-Host '2. CONNECT'
        Write-Host '3. DEPLOY'
        Write-Host '4. UPDATE'
        Write-Host
        $choice = Read-Host 'Select'
        Write-Host
        try {
            switch ($choice) {
                '1' { Select-HostRole }
                '2' { Connect-ToLanHost }
                '3' {
                    if ($script:LanRole -ne 'HOST') {
                        throw 'DEPLOY is available only after selecting HOST.'
                    }
                    Publish-Build $false
                }
                '4' {
                    if (-not $script:IsConnected) {
                        throw 'Select HOST or CONNECT before UPDATE.'
                    }
                    Update-LocalBuild
                }
                default { Write-Host 'Unknown menu item.' -ForegroundColor Yellow }
            }
        } catch {
            Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
        }
        Write-Host
        Read-Host 'Press Enter to continue' | Out-Null
    }
} catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($script:OwnsLanServer) {
        try {
            Stop-DeltaServer
        } catch {
            # The hidden server also monitors this process and will stop itself
            # if the console is closed before this cleanup can run.
        }
    }
}
