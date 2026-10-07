# Undoes the oldest AI turn in the sandbox and prints the folder before/after. No HTTP, no Gemini.
$bin = "$PSScriptRoot\..\HearthFileManager\bin"
Add-Type -Path "$bin\Newtonsoft.Json.dll"; Add-Type -Path "$bin\System.Data.SQLite.dll"; Add-Type -Path "$bin\HearthFileManager.dll"
$ad = "$PSScriptRoot\sandbox\App_Data"
$fs = New-Object HearthFileManager.engine.FsService($ad, "$ad\public")
"before: " + (($fs.List("") | % { $_.Path }) -join ", ")
$id = (Get-ChildItem "$ad\ai-undo" | Sort-Object CreationTime | Select -First 1).Name
"undo $id -> " + ([HearthFileManager.engine.Ai.AiUndo]::Undo($fs, $id) -join ", ")
"after: " + (($fs.List("") | % { $_.Path }) -join ", ") + " | App_Data: " + (($fs.List("App_Data") | % { $_.Path }) -join ", ")
