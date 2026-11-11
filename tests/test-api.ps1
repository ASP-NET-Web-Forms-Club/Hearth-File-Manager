# Tests the deploy API (/api/v1) over HTTP against a sandbox copy of the site. No Gemini.
#   1. build the project
#   2. copy bin, css, js, assets, Global.asax, Web.config to tests\sandbox\site with App_Data\config.json = {"Users":[]}
#   3. "C:\Program Files\IIS Express\iisexpress.exe" /path:<...>\tests\sandbox\site /port:8791 /clr:v4.0
#   4. powershell -ExecutionPolicy Bypass -File tests\test-api.ps1
param([string]$BaseUrl = "http://localhost:8791", [string]$Site = "$PSScriptRoot\sandbox\site")
$ErrorActionPreference = "Stop"
$bin = "$PSScriptRoot\..\HearthFileManager\bin"
Add-Type -Path "$bin\Newtonsoft.Json.dll"; Add-Type -Path "$bin\System.Data.SQLite.dll"; Add-Type -Path "$bin\HearthFileManager.dll"
Add-Type -AssemblyName System.IO.Compression; Add-Type -AssemblyName System.IO.Compression.FileSystem

$fails = 0
function Check($name, $ok, $detail) { if ($ok) { Write-Host "PASS  $name" -ForegroundColor Green } else { $script:fails++; Write-Host "FAIL  $name  $detail" -ForegroundColor Red } }

# Calls the API; returns @{ Status; Json } and never throws on 4xx/5xx.
function Call($method, $url, $token, $body, $contentType) {
    $req = [Net.HttpWebRequest]::Create("$BaseUrl/api/v1/$url")
    $req.Method = $method
    if ($token) { $req.Headers["Authorization"] = "Bearer $token" }
    if ($null -ne $body) {
        if ($body -is [string]) { $body = [Text.Encoding]::UTF8.GetBytes($body) }
        $req.ContentType = $contentType; $req.ContentLength = $body.Length
        $s = $req.GetRequestStream(); $s.Write($body, 0, $body.Length); $s.Close()
    } elseif ($method -eq "POST") { $req.ContentLength = 0 }
    try { $res = $req.GetResponse() } catch [Net.WebException] { $res = $_.Exception.Response; if (-not $res) { throw } }
    $text = (New-Object IO.StreamReader($res.GetResponseStream())).ReadToEnd()
    $status = [int]$res.StatusCode; $res.Close()
    $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
    return @{ Status = $status; Json = $json; Text = $text }
}

# Builds a zip in memory from @{ "entry/name" = "content" }.
function NewZip($entries) {
    $ms = New-Object IO.MemoryStream
    $zip = New-Object IO.Compression.ZipArchive($ms, [IO.Compression.ZipArchiveMode]::Create, $true)
    foreach ($k in $entries.Keys) {
        $w = New-Object IO.StreamWriter($zip.CreateEntry($k).Open()); $w.Write($entries[$k]); $w.Dispose()
    }
    $zip.Dispose()
    return ,$ms.ToArray()
}
function Sha($text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    return -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString("x2") })
}

# --- set-up: users and tokens are written straight into the sandbox App_Data -------------
$AC = [HearthFileManager.engine.AppConfig]; $AT = [HearthFileManager.engine.ApiTokens]
$AC::AppDataPath = "$Site\App_Data"
$AC::Update([Action[HearthFileManager.engine.obConfig]] {
    param($c)
    $c.Users.RemoveAll([Predicate[HearthFileManager.engine.obUser]] { param($u) $u.Username -ne "admin" }) | Out-Null
    foreach ($spec in @(@("alex", "alex", @("files", "api")), @("viewer", "", @("api")), @("noapi", "", @("files")))) {
        $u = New-Object HearthFileManager.engine.obUser
        $u.Username = $spec[0]; $u.RootPath = $spec[1]; $u.PasswordHash = "x"; $u.CreatedUtc = [DateTime]::UtcNow
        $u.Permissions = New-Object 'System.Collections.Generic.List[string]'; $spec[2] | ForEach-Object { $u.Permissions.Add($_) }
        $c.Users.Add($u)
    }
})
foreach ($n in "admin", "alex", "viewer", "noapi") { $AT::RevokeUser($n) }
$plain = ""
function NewToken($user, $write, $days) { $p = ""; $t = $AT::Create($user, "test", $write, $days, [ref]$p); return @{ Token = $p; Info = $t } }
$admin = (NewToken "admin" $true 0).Token
$alex = (NewToken "alex" $true 30).Token
$alexRO = (NewToken "alex" $false 0).Token
$viewer = (NewToken "viewer" $true 0).Token      # write token, but the user lacks "files"
$noapi = (NewToken "noapi" $true 0).Token
$root = "$Site\App_Data\public"
Start-Sleep -Milliseconds 1200   # file stamp granularity: let the server see the new config/tokens

# --- storage ---------------------------------------------------------------------------
$stored = Get-Content "$Site\App_Data\api-tokens.json" -Raw
Check "token file holds no plain token"      (-not $stored.Contains($alex) -and -not $stored.Contains($alex.Substring(10))) ""
Check "token looks like hfm_ + 64 hex"       ($alex -match '^hfm_[0-9a-f]{64}$') $alex.Length

# --- authentication ----------------------------------------------------------------------
Check "no token -> 401"                      ((Call GET "whoami" $null).Status -eq 401) ""
Check "wrong token -> 401"                   ((Call GET "whoami" ("hfm_" + "0" * 64)).Status -eq 401) ""
Check "user without API permission -> 403"   ((Call GET "whoami" $noapi).Status -eq 403) ""
$r = Call GET "whoami" $alex
Check "whoami ok"                            ($r.Status -eq 200 -and $r.Json.data.User -eq "alex" -and $r.Json.data.CanWrite) $r.Text
Check "read-only token reports CanWrite=false" (-not (Call GET "whoami" $alexRO).Json.data.CanWrite) ""
Check "unknown endpoint -> 404"((Call GET "nosuch" $alex).Status -eq 404) ""
Check "POST-only endpoint refuses GET"       ((Call GET "upload" $alex).Status -eq 405) ""

# --- upload ------------------------------------------------------------------------------
$zip = NewZip @{ "index.php" = "<?php echo 1;"; "css/site.css" = "body{}"; "img\logo.txt" = "logo" }
$r = Call POST "upload?path=" $alex $zip "application/zip"
Check "upload extracts 3 files"              ($r.Status -eq 200 -and $r.Json.data.Files -eq 3) $r.Text
Check "files land in alex's own root"        ((Test-Path "$root\alex\index.php") -and (Test-Path "$root\alex\css\site.css") -and (Test-Path "$root\alex\img\logo.txt")) ""
Check "nothing written to the main root"     (-not (Test-Path "$root\index.php")) ""
$r = Call POST "upload?path=sub/deep" $alex (NewZip @{ "a.txt" = "A" }) "application/zip"
Check "upload into a new sub-folder"         ($r.Status -eq 200 -and (Test-Path "$root\alex\sub\deep\a.txt")) $r.Text
$r = Call POST "upload?path=" $alex (NewZip @{ "index.php" = "<?php echo 2;" }) "application/zip"
Check "overwrite ok"                         ($r.Status -eq 200 -and (Get-Content "$root\alex\index.php" -Raw) -eq "<?php echo 2;") $r.Text
Check "old copy went to the recycle bin"     (@(Get-ChildItem "$Site\App_Data\recycle-bin" -Filter "index.php.api.*").Count -eq 1) ""

$r = Call POST "upload?path=" $alex (NewZip @{ "../escape.txt" = "x" }) "application/zip"
Check "zip-slip entry refused"               ($r.Status -eq 400 -and -not (Test-Path "$root\escape.txt")) $r.Text
$r = Call POST "upload?path=../.." $alex $zip "application/zip"
Check "target outside the root refused"      ($r.Status -eq 400) $r.Text
$r = Call POST ('upload?path=' + [uri]::EscapeDataString('$recycle')) $alex $zip "application/zip"
Check "upload into recycle bin refused"      ($r.Status -eq 400) $r.Text
$r = Call POST "upload?path=" $alex (NewZip @{ '$recycle/x.txt' = "x" }) "application/zip"
Check "zip entry into recycle bin refused"   ($r.Status -eq 400) $r.Text
$r = Call POST "upload?path=" $alex ([Text.Encoding]::UTF8.GetBytes("this is not a zip")) "application/zip"
Check "non-zip body refused"                 ($r.Status -eq 400 -and $r.Json.message -match "valid zip") $r.Text
Check "empty body refused"                   ((Call POST "upload?path=" $alex).Status -eq 400) ""
Check "read-only token cannot upload"        ((Call POST "upload?path=" $alexRO $zip "application/zip").Status -eq 403) ""
Check "user without 'files' cannot upload"   ((Call POST "upload?path=" $viewer $zip "application/zip").Status -eq 403) ""
Check "no temp files left behind"            (@(Get-ChildItem "$Site\App_Data\tmp" -Filter "api*.part").Count -eq 0) ""

# chunked: one zip sent in 3 parts
$big = NewZip @{ "big/one.txt" = ("1" * 5000); "big/two.txt" = ("2" * 5000); "big/three.bin" = [Guid]::NewGuid().ToString() * 40 }
$n = [int][Math]::Ceiling($big.Length / 3); $id = [Guid]::NewGuid().ToString("N")
for ($i = 0; $i -lt 3; $i++) {
    $part = $big[($i * $n)..([Math]::Min($big.Length, ($i + 1) * $n) - 1)]
    $r = Call POST "upload?path=&uploadId=$id&index=$i&total=3" $alex ([byte[]]$part) "application/octet-stream"
}
Check "chunked upload extracts"              ($r.Status -eq 200 -and $r.Json.data.Files -eq 3 -and (Get-Content "$root\alex\big\two.txt" -Raw) -eq ("2" * 5000)) $r.Text

# extraction cap (zip bomb): 1 MB limit, 3 MB of zeros
$AC::Update([Action[HearthFileManager.engine.obConfig]] { param($c) $c.ApiMaxExtractMb = 1 }); Start-Sleep -Milliseconds 1200
$r = Call POST "upload?path=" $alex (NewZip @{ "bomb.txt" = ("0" * 3MB) }) "application/zip"
Check "zip bomb refused"                     ($r.Status -eq 400 -and -not (Test-Path "$root\alex\bomb.txt")) $r.Text
$AC::Update([Action[HearthFileManager.engine.obConfig]] { param($c) $c.ApiMaxExtractMb = 2000 }); Start-Sleep -Milliseconds 1200

# --- list --------------------------------------------------------------------------------
$r = Call GET "list?path=" $alex
$names = @($r.Json.items | ForEach-Object { $_.Path })
Check "list (flat) shows top level only"     ($r.Status -eq 200 -and $names -contains "index.php" -and $names -contains "css" -and $names -notcontains "css/site.css") ($names -join ",")
$r = Call GET "list?path=&recursive=1&hash=1" $alex
$css = $r.Json.items | Where-Object { $_.Path -eq "css/site.css" }
Check "recursive list with hash"             ($css -and $css.Sha256 -eq (Sha "body{}") -and $css.Size -eq 6) ($css | ConvertTo-Json -Compress)
Check "dates are UTC ISO"                    ("$($r.Text)" -match '"ModifiedUtc":"\d{4}-\d\d-\d\dT[\d:.]+Z"') ""
Check "folders have no hash"                 (-not ($r.Json.items | Where-Object { $_.IsDir -and $_.Sha256 })) ""
$r = Call GET "list?path=css" $alex
Check "list a sub-folder"                    ($r.Json.items.Count -eq 1 -and $r.Json.items[0].Path -eq "css/site.css") $r.Text
Check "read-only token can list"             ((Call GET "list?path=" $alexRO).Status -eq 200) ""
Check "list outside the root refused"        ((Call GET "list?path=../" $alex).Status -eq 400) ""
Check "list missing folder -> 400"           ((Call GET "list?path=nope" $alex).Status -eq 400) ""
Check "recycle bin not listable"             ((Call GET ('list?path=' + [uri]::EscapeDataString('$recycle')) $alex).Status -eq 400) ""
$r = Call GET "list?path=&recursive=1" $admin
Check "admin (main root) sees alex/ folder"  (@($r.Json.items | Where-Object { $_.Path -eq "alex/index.php" }).Count -eq 1) ""

# --- list: exclude folders ---------------------------------------------------------------
foreach ($d in "uploads\receipts", "shop\uploads", "storage\receipts", "storage\keep", "cache-a") { New-Item -ItemType Directory -Force "$root\alex\$d" | Out-Null }
foreach ($f in "uploads\receipts\r1.pdf", "uploads\u1.jpg", "shop\uploads\u2.jpg", "shop\shop.php", "storage\receipts\r2.pdf", "storage\keep\k.txt", "cache-a\c.tmp") { Set-Content "$root\alex\$f" "x" }
function Paths($url) { $r = Call GET $url $alex; return @{ R = $r; P = @($r.Json.items | ForEach-Object { $_.Path }) } }
$x = Paths "list?path=&recursive=1&exclude=uploads"
Check "exclude by name: any depth"           ($x.P -notcontains "uploads/u1.jpg" -and $x.P -notcontains "uploads/receipts/r1.pdf" -and $x.P -notcontains "shop/uploads/u2.jpg" -and $x.P -contains "shop/shop.php") ($x.P -join ",")
$flag = @($x.R.Json.items | Where-Object { $_.Excluded } | ForEach-Object { $_.Path })
Check "excluded folders listed once, flagged" (($flag -join ",") -eq "shop/uploads,uploads") ($flag -join ",")
Check "other folders are not flagged"        (-not ($x.R.Json.items | Where-Object { $_.Path -eq "css" }).Excluded) ""
$x = Paths "list?path=&recursive=1&exclude=storage/receipts"
Check "exclude by path: only that folder"    ($x.P -notcontains "storage/receipts/r2.pdf" -and $x.P -contains "storage/keep/k.txt" -and $x.P -contains "uploads/receipts/r1.pdf") ($x.P -join ",")
$x = Paths "list?path=&recursive=1&exclude=UPLOADS,cache-*,storage/receipts"
Check "several patterns, wildcard, any case" ($x.P -notcontains "uploads/u1.jpg" -and $x.P -notcontains "cache-a/c.tmp" -and $x.P -notcontains "storage/receipts/r2.pdf" -and $x.P -contains "index.php") ($x.P -join ",")
$x = Paths "list?path=&recursive=1&exclude=uploads&exclude=cache-a"
Check "repeated exclude parameter"           ($x.P -notcontains "uploads/u1.jpg" -and $x.P -notcontains "cache-a/c.tmp") ($x.P -join ",")
$x = Paths "list?path=&recursive=1&exclude=up"
Check "pattern must match the whole name"    ($x.P -contains "uploads/u1.jpg") ""
$x = Paths "list?path=&recursive=1&exclude=*/receipts"
Check "wildcard does not cross folders"      ($x.P -notcontains "storage/receipts/r2.pdf" -and $x.P -notcontains "uploads/receipts/r1.pdf" -and $x.P -contains "storage/keep/k.txt") ($x.P -join ",")
$x = Paths "list?path=storage&recursive=1&exclude=storage/receipts"
Check "path patterns are from the root"      ($x.P -notcontains "storage/receipts/r2.pdf" -and $x.P -contains "storage/keep/k.txt") ($x.P -join ",")
$x = Paths "list?path=&exclude=uploads"
Check "flat list flags the folder too"       (($x.R.Json.items | Where-Object { $_.Path -eq "uploads" }).Excluded) ""
$x = Paths "list?path=&recursive=1&exclude=..,/,"
Check "odd patterns are harmless"            ($x.R.Status -eq 200 -and $x.P -contains "uploads/u1.jpg") $x.R.Text
foreach ($d in "uploads", "shop", "storage", "cache-a") { Remove-Item "$root\alex\$d" -Recurse -Force }

# --- delete ------------------------------------------------------------------------------
$r = Call POST "delete" $alex '{"paths":["img/logo.txt","sub","does-not-exist.txt"]}' "application/json"
Check "delete moves to recycle bin"          ($r.Status -eq 200 -and $r.Json.data.Deleted.Count -eq 2 -and $r.Json.data.NotFound.Count -eq 1 -and -not (Test-Path "$root\alex\sub")) $r.Text
Check "deleted items are recoverable"        (@(Get-ChildItem "$Site\App_Data\recycle-bin" | Where-Object { $_.Name -like "img_logo.txt.api.*" -or $_.Name -like "sub.api.*" }).Count -eq 2) ""
$r = Call POST "delete" $alex '{"paths":["css/site.css","../../config.json"]}' "application/json"
Check "bad path aborts the whole delete"     ($r.Status -eq 400 -and (Test-Path "$root\alex\css\site.css") -and (Test-Path "$Site\App_Data\config.json")) $r.Text
Check "root cannot be deleted"               ((Call POST "delete" $alex '{"paths":[""]}' "application/json").Status -eq 400) ""
Check "recycle bin cannot be emptied"        ((Call POST "delete" $alex '{"paths":["$recycle"]}' "application/json").Status -eq 400) ""
Check "read-only token cannot delete"        ((Call POST "delete" $alexRO '{"paths":["index.php"]}' "application/json").Status -eq 403 -and (Test-Path "$root\alex\index.php")) ""
Check "garbage body -> 400"                  ((Call POST "delete" $alex 'not json' "application/json").Status -eq 400) ""

# --- token lifecycle ---------------------------------------------------------------------
$exp = NewToken "alex" $true 1
$list = [Newtonsoft.Json.JsonConvert]::DeserializeObject((Get-Content "$Site\App_Data\api-tokens.json" -Raw))
foreach ($t in $list) { if ($t["Id"].ToString() -eq $exp.Info.Id) { $t["ExpiresUtc"] = [DateTime]::UtcNow.AddMinutes(-1) } }
Start-Sleep -Milliseconds 1200
[IO.File]::WriteAllText("$Site\App_Data\api-tokens.json", $list.ToString())
Start-Sleep -Milliseconds 1200
Check "expired token -> 401"                 ((Call GET "whoami" $exp.Token).Status -eq 401) ""
Check "other tokens still work"              ((Call GET "whoami" $alex).Status -eq 200) ""
$roId = ($AT::ListFor("alex") | Where-Object { -not $_.CanWrite }).Id
Check "revoke returns true"                  ($AT::Revoke("alex", $roId)) ""
Check "cannot revoke another user's token"   (-not $AT::Revoke("viewer", ($AT::ListFor("alex"))[0].Id)) ""
Start-Sleep -Milliseconds 1200
Check "revoked token -> 401"                 ((Call GET "whoami" $alexRO).Status -eq 401) ""

# --- help and audit log ------------------------------------------------------------------
$r = Call GET "help" $alex
Check "help is plain text with the base URL" ($r.Status -eq 200 -and $r.Text.Contains("$BaseUrl/api/v1") -and -not $r.Text.Contains($alex)) ""
$log = Get-Content "$Site\App_Data\api-log.jsonl" -Raw
Check "audit log records calls"              ($log -match '"action":"upload"' -and $log -match '"user":"alex"' -and $log -match '"status":401') ""
Check "audit log holds no token"             (-not $log.Contains($alex.Substring(10))) ""

Write-Host ""
if ($fails) { Write-Host "$fails test(s) failed" -ForegroundColor Red; exit 1 } else { Write-Host "All tests passed" -ForegroundColor Green }
