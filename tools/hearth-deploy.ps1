<#
.SYNOPSIS
    Publishes a local folder to a Hearth File Manager server through its deploy API (/api/v1).

.DESCRIPTION
    Compares the local files with the server by SHA-256, zips only the new and changed files,
    uploads the zip, and (optionally) moves files that no longer exist locally to Hearth's recycle bin.

    The access token is read from the environment variable HEARTH_TOKEN. Create one in Hearth under
    My Account > Access tokens. Never write the token into this script.

.EXAMPLE
    $env:HEARTH_TOKEN = '<paste token>'      # once per PowerShell window
    .\hearth-deploy.ps1 -BaseUrl https://files.example.com -LocalDir C:\sites\shop -DryRun
    .\hearth-deploy.ps1 -BaseUrl https://files.example.com -LocalDir C:\sites\shop -RemotePath shop -DeleteRemoved
    .\hearth-deploy.ps1 -BaseUrl https://files.example.com -LocalDir C:\sites\shop -ExcludeFolders uploads,storage/receipts -DeleteRemoved
#>
param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [string]$LocalDir = ".",
    # Folder on the server, relative to your Hearth root. Empty = the root itself.
    [string]$RemotePath = "",
    # Wildcards matched against the relative path ("/" separators). These files are never uploaded or deleted.
    [string[]]$Exclude = @('.git/*', '.vs/*', '.claude/*', 'node_modules/*', '*.ps1', '.env', '*/.env', 'web.config'),
    # Server folders to leave alone: they are not listed, compared, uploaded into or deleted from.
    # Use this for folders that only matter on the live server (customer uploads, receipts, caches).
    #   'uploads'           any folder with that name, at any depth
    #   'storage/receipts'  exactly that folder (path from your Hearth root); "*" is a wildcard
    [string[]]$ExcludeFolders = @(),
    # Also move server files that do not exist locally to the recycle bin.
    [switch]$DeleteRemoved,
    # Only show what would happen.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if (-not $env:HEARTH_TOKEN) { throw "Set the environment variable HEARTH_TOKEN to your Hearth access token first." }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$api = $BaseUrl.TrimEnd('/') + '/api/v1'
$headers = @{ Authorization = "Bearer $env:HEARTH_TOKEN" }
$RemotePath = $RemotePath.Replace('\', '/').Trim('/')
$localRoot = (Resolve-Path -LiteralPath $LocalDir).Path.TrimEnd('\')

function Invoke-Hearth {
    param([string]$Method, [string]$Url, $Body, [string]$ContentType, [string]$InFile)
    $p = @{ Method = $Method; Uri = "$api/$Url"; Headers = $headers }
    if ($ContentType) { $p.ContentType = $ContentType }
    if ($InFile) { $p.InFile = $InFile } elseif ($null -ne $Body) { $p.Body = $Body }
    try { return Invoke-RestMethod @p }
    catch {
        # Hearth answers errors with JSON: { success: false, message: "..." }
        $msg = $_.ErrorDetails.Message
        if (-not $msg -and $_.Exception.Response -and $_.Exception.Response.GetResponseStream) {
            # Windows PowerShell 5.1 often leaves ErrorDetails empty: read the body ourselves
            try {
                $stream = $_.Exception.Response.GetResponseStream()
                if ($stream.CanSeek) { $stream.Position = 0 }
                $msg = (New-Object IO.StreamReader($stream)).ReadToEnd()
            } catch { }
        }
        if ($msg) { try { $msg = ($msg | ConvertFrom-Json).message } catch { } }
        if (-not $msg) { $msg = $_.Exception.Message }
        throw "Hearth API ($Url): $msg"
    }
}

function Test-Excluded([string]$rel) {
    foreach ($pattern in $Exclude) { if ($rel -like $pattern) { return $true } }
    return $false
}

# Same matching rules as the server's "exclude" parameter.
$prefix = if ($RemotePath) { "$RemotePath/" } else { "" }
$excludeRx = @($ExcludeFolders | ForEach-Object { $_.Replace('\', '/').Trim('/') } | Where-Object { $_ } | ForEach-Object {
    $body = [regex]::Escape($_).Replace('\*', '[^/]*')
    if ($_.Contains('/')) { "^$body`$" } else { "(^|/)$body`$" }
})
function Test-InExcludedFolder([string]$rel) {
    if ($excludeRx.Count -eq 0) { return $false }
    $parts = $rel.Split('/')
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        $folder = $prefix + ($parts[0..$i] -join '/')
        foreach ($rx in $excludeRx) { if ($folder -match $rx) { return $true } }
    }
    return $false
}

# ---- 1. token check ---------------------------------------------------------
$me = (Invoke-Hearth GET 'whoami').data
Write-Host "Signed in as $($me.User) with token '$($me.TokenName)'."
if (-not $me.CanWrite -and -not $DryRun) { throw "This token is read-only. Create a read & write token, or use -DryRun." }

# ---- 2. local files ---------------------------------------------------------
$local = @{}
Get-ChildItem -LiteralPath $localRoot -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($localRoot.Length + 1).Replace('\', '/')
    if (-not (Test-Excluded $rel) -and -not (Test-InExcludedFolder $rel)) { $local[$rel] = $_.FullName }
}

# ---- 3. server files --------------------------------------------------------
$remote = @{}
$query = "list?path=$([uri]::EscapeDataString($RemotePath))&recursive=1&hash=1"
if ($ExcludeFolders.Count) { $query += "&exclude=" + [uri]::EscapeDataString(($ExcludeFolders -join ',')) }
try { $items = (Invoke-Hearth GET $query).items }
catch { if ("$_" -match 'Folder not found') { $items = @() } else { throw } }   # first deployment: folder does not exist yet
foreach ($it in $items) {
    if ($it.IsDir) { continue }
    $rel = $it.Path.Substring($prefix.Length)
    $remote[$rel] = $it.Sha256
}

# ---- 4. compare -------------------------------------------------------------
$changed = @()
foreach ($rel in ($local.Keys | Sort-Object)) {
    $hash = (Get-FileHash -LiteralPath $local[$rel] -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $remote.ContainsKey($rel) -or $remote[$rel] -ne $hash) { $changed += $rel }
}
$removed = @()
if ($DeleteRemoved) {
    $removed = @($remote.Keys | Where-Object { -not $local.ContainsKey($_) -and -not (Test-Excluded $_) } | Sort-Object)
}

Write-Host "$($local.Count) local file(s), $($remote.Count) on the server."
$skipped = @($items | Where-Object { $_.Excluded } | ForEach-Object { $_.Path })
if ($skipped.Count) { Write-Host "Left alone (excluded folders): $($skipped -join ', ')" }
Write-Host "To upload: $($changed.Count)"
$changed | ForEach-Object { Write-Host "  + $_" }
if ($DeleteRemoved) {
    Write-Host "To delete (moved to the recycle bin): $($removed.Count)"
    $removed | ForEach-Object { Write-Host "  - $_" }
}
if ($DryRun) { Write-Host "Dry run: nothing was changed."; return }
if ($changed.Count -eq 0 -and $removed.Count -eq 0) { Write-Host "Already up to date."; return }

# ---- 5. zip and upload ------------------------------------------------------
if ($changed.Count -gt 0) {
    $zipPath = Join-Path ([IO.Path]::GetTempPath()) ("hearth-deploy-" + [Guid]::NewGuid().ToString('N') + ".zip")
    try {
        $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($rel in $changed) {
                # entry names use "/" and keep the folder structure
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $local[$rel], $rel, [IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally { $zip.Dispose() }

        $size = (Get-Item -LiteralPath $zipPath).Length
        $target = "upload?path=$([uri]::EscapeDataString($RemotePath))"
        $partSize = [int][Math]::Max(1MB, [Math]::Floor($me.MaxRequestBytes / 2))
        if ($size -le $partSize) {
            $r = Invoke-Hearth POST $target -InFile $zipPath -ContentType 'application/zip'
        }
        else {
            # too big for one request: send it in parts, the server extracts after the last one
            $uploadId = [Guid]::NewGuid().ToString('N')
            $total = [int][Math]::Ceiling($size / $partSize)
            $fs = [IO.File]::OpenRead($zipPath)
            try {
                for ($i = 0; $i -lt $total; $i++) {
                    $buf = New-Object byte[] ([int][Math]::Min($partSize, $size - $fs.Position))
                    $read = 0
                    while ($read -lt $buf.Length) { $read += $fs.Read($buf, $read, $buf.Length - $read) }
                    Write-Host "  uploading part $($i + 1) of $total"
                    $r = Invoke-Hearth POST "$target&uploadId=$uploadId&index=$i&total=$total" -Body $buf -ContentType 'application/octet-stream'
                }
            }
            finally { $fs.Dispose() }
        }
        Write-Host $r.message
    }
    finally { if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force } }
}

# ---- 6. delete --------------------------------------------------------------
if ($removed.Count -gt 0) {
    $body = @{ paths = @($removed | ForEach-Object { "$prefix$_" }) } | ConvertTo-Json
    $r = Invoke-Hearth POST 'delete' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json'
    Write-Host $r.message
}

# ---- 7. verify --------------------------------------------------------------
$after = @{}
foreach ($it in (Invoke-Hearth GET $query).items) { if (-not $it.IsDir) { $after[$it.Path.Substring($prefix.Length)] = $it.Sha256 } }
$bad = @($changed | Where-Object { $after[$_] -ne (Get-FileHash -LiteralPath $local[$_] -Algorithm SHA256).Hash.ToLowerInvariant() })
$left = @($removed | Where-Object { $after.ContainsKey($_) })
if ($bad.Count -or $left.Count) {
    $bad | ForEach-Object { Write-Host "  MISMATCH $_" -ForegroundColor Red }
    $left | ForEach-Object { Write-Host "  NOT DELETED $_" -ForegroundColor Red }
    throw "Deployment finished, but the server does not match the local files."
}
Write-Host "Verified: the server matches the local files." -ForegroundColor Green
