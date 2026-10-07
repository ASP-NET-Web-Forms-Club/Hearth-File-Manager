# Tests root-folder resolution, per-root isolation and the shared recycle bin. No IIS, no Gemini.
$ErrorActionPreference = "Stop"
$bin = "$PSScriptRoot\..\HearthFileManager\bin"
Add-Type -Path "$bin\Newtonsoft.Json.dll"; Add-Type -Path "$bin\System.Data.SQLite.dll"; Add-Type -Path "$bin\HearthFileManager.dll"

$app = "$PSScriptRoot\sandbox\hearth"            # pretend Hearth folder
$ad  = "$app\App_Data"
if (Test-Path "$PSScriptRoot\sandbox") { Remove-Item "$PSScriptRoot\sandbox" -Recurse -Force }
New-Item -ItemType Directory -Force $ad | Out-Null
$AC = [HearthFileManager.engine.AppConfig]
$AC::AppDataPath = $ad
$main = $AC::ResolvePath("/App_Data/public", $AC::AppRoot)

$fails = 0
function Check($name, $ok, $detail) { if ($ok) { Write-Host "PASS  $name" -ForegroundColor Green } else { $script:fails++; Write-Host "FAIL  $name  $detail" -ForegroundColor Red } }
function Throws($block) { try { & $block; return $false } catch { return $true } }

# --- path resolution -------------------------------------------------------
Check "app-relative /App_Data/public"   ($main -eq "$((Resolve-Path $app).Path)\App_Data\public") $main
Check "backslash form same result"      ($AC::ResolvePath("\App_Data\public", $AC::AppRoot) -eq $main) ""
Check "~/ form same result"             ($AC::ResolvePath("~/App_Data/public", $AC::AppRoot) -eq $main) ""
Check "sub-folder relative to main"     ($AC::ResolvePath("alex", $main) -eq "$main\alex") ""
Check "absolute with forward slashes"   ($AC::ResolvePath("D:/websites/alex-site/", $main) -eq "D:\websites\alex-site") ""
Check "drive root kept"                 ($AC::ResolvePath("C:\", $main) -eq "C:\") ""
Check "normalize absolute"              ($AC::NormalizePathSetting("D:/websites//alex/") -eq "D:\websites\alex") ""
Check "normalize relative"              ($AC::NormalizePathSetting("\App_Data\public\alex\") -eq "/App_Data/public/alex") ""

# --- forbidden roots ---------------------------------------------------------
Check "root = Hearth folder rejected"   ($AC::RootProblem($AC::AppRoot) -ne $null) ""
Check "root = drive (contains Hearth)"  ($AC::RootProblem(($AC::AppRoot).Substring(0,3)) -ne $null) ""
Check "root = App_Data rejected"        ($AC::RootProblem($ad) -ne $null) ""
Check "root = App_Data\recycle-bin no"  ($AC::RootProblem("$ad\recycle-bin\x") -ne $null) ""
Check "root = App_Data\public ok"       ($AC::RootProblem($main) -eq $null) ""
Check "root = other drive folder ok"    ($AC::RootProblem("D:\websites\alex") -eq $null) ""

# --- isolation + recycle bin -------------------------------------------------
$adminFs = New-Object HearthFileManager.engine.FsService($ad, $main)
$alexFs  = New-Object HearthFileManager.engine.FsService($ad, "$main\alex")
$otherFs = New-Object HearthFileManager.engine.FsService($ad, "$PSScriptRoot\sandbox\other-site")
Check "write probe ok"                  ($adminFs.WriteProblem() -eq $null) ""

$adminFs.WriteText("index.php", "<?php echo 1;")
$alexFs.WriteText("alex.txt", "a")
$otherFs.WriteText("other.txt", "o")
Check "admin sees alex folder"          (($adminFs.List("") | % Name) -contains "alex") ""
Check "alex cannot escape (..)"         (Throws { $alexFs.Resolve("..\index.php") }) ""
Check "alex cannot escape (abs)"        (Throws { $alexFs.Resolve("C:\Windows") }) ""
Check "cannot delete root"              (Throws { $alexFs.Delete("", "user") }) ""
Check "cannot delete recycle bin"       (Throws { $alexFs.Delete('$recycle', "user") }) ""

$alexBin  = $alexFs.Delete("alex.txt", "user")
$otherBin = $otherFs.Delete("other.txt", "gemini")
Check "deleted into private bin"        (Test-Path "$ad\recycle-bin\$(Split-Path $alexBin -Leaf)") $alexBin
Check "alex sees own deleted item"      ((($alexFs.List('$recycle')) | % OriginalPath) -contains "alex.txt") ""
Check "alex does not see other's"       (-not ((($alexFs.List('$recycle')) | % OriginalPath) -contains "other.txt")) ""
Check "admin sees alex's (inside main)" ((($adminFs.List('$recycle')) | % OriginalPath) -contains "alex/alex.txt") ""
Check "admin does not see other root"   (-not ((($adminFs.List('$recycle')) | % OriginalPath) -match "other")) ""
Check "alex cannot open other's item"   (Throws { $alexFs.Resolve($otherBin) }) $otherBin
Check "alex cannot restore other's"     (Throws { $alexFs.Restore($otherBin) }) ""

$restored = $alexFs.Restore($alexBin)
Check "restore to original"             ($restored -eq "alex.txt" -and (Test-Path "$main\alex\alex.txt")) $restored
$n = $alexFs.EmptyRecycleBin()
Check "empty only own items"            ((Test-Path "$ad\recycle-bin\$(Split-Path $otherBin -Leaf)")) "other's item was removed"

if ($fails -eq 0) { Write-Host "`nAll tests passed." -ForegroundColor Green } else { Write-Host "`n$fails test(s) failed." -ForegroundColor Red }
Remove-Item "$PSScriptRoot\sandbox" -Recurse -Force
