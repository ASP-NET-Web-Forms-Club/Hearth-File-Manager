# Runs one Gemini turn through the compiled engine, without IIS/HTTP.
# Usage: powershell -File test-gemini.ps1 -Prompt "write a simple under maintenance page" [-AppData <folder>]
# The API key, model and rate limit are read from the project's App_Data/config.json.
param(
    [string]$Prompt = "List the files in www and tell me what you see.",
    [string]$AppData = "$PSScriptRoot\sandbox\App_Data",
    [string]$Model = ""
)
$ErrorActionPreference = "Stop"
$proj = Resolve-Path "$PSScriptRoot\..\HearthFileManager"
$bin = "$proj\bin"

# SQLite needs its native interop dll next to the managed one; point it at bin\
$env:PreLoadSQLite_BaseDirectory = $bin
Add-Type -Path "$bin\Newtonsoft.Json.dll"
Add-Type -Path "$bin\System.Data.SQLite.dll"
Add-Type -Path "$bin\HearthFileManager.dll"

# Config comes from the real App_Data; files go to the sandbox folder.
[HearthFileManager.engine.AppConfig]::AppDataPath = "$proj\App_Data"
$cfg = [HearthFileManager.engine.AppConfig]::Get()

New-Item -ItemType Directory -Force $AppData | Out-Null
$fs = New-Object HearthFileManager.engine.FsService($AppData)
$useModel = if ($Model) { $Model } else { $cfg.GeminiModel }
Write-Host "Model: $useModel"
$agent = New-Object HearthFileManager.engine.Ai.GeminiAgent($fs, $cfg.GeminiApiKey, $useModel, $cfg.GeminiRpm, $cfg.SitePreviewUrl)
$agent.FallbackModels = $cfg.GeminiFallbackModels
$agent.OnProgress = [Action[string,string]]{ param($k, $t) Write-Host "  [$k] $t" -ForegroundColor DarkGray }

$parts = New-Object Newtonsoft.Json.Linq.JArray
$p = New-Object Newtonsoft.Json.Linq.JObject
$p["text"] = New-Object Newtonsoft.Json.Linq.JValue([string]$Prompt)
$parts.Add($p)

$historyFile = "$AppData\..\history.json"
$history = if (Test-Path $historyFile) { [Newtonsoft.Json.Linq.JArray]::Parse((Get-Content $historyFile -Raw)) } else { New-Object Newtonsoft.Json.Linq.JArray }

$turnId = "test-" + (Get-Date -Format "yyyyMMdd-HHmmss")
$sw = [Diagnostics.Stopwatch]::StartNew()
$r = $agent.RunTurn($history, $parts, $turnId, [Threading.CancellationToken]::None)
$sw.Stop()

[IO.File]::WriteAllText($historyFile, $r.Contents.ToString())
Write-Host "`n=== Reply ($($r.Requests) requests, $([int]$sw.Elapsed.TotalSeconds)s) ===" -ForegroundColor Cyan
Write-Host $r.FinalText
if ($r.Error) { Write-Host "ERROR: $($r.Error)" -ForegroundColor Red }
Write-Host "Changed: $($r.Changed -join ', ')"
Write-Host "UndoId:  $($r.UndoId)"
