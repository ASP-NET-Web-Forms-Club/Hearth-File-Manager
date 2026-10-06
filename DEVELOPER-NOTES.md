# Hearth File Manager

Pageless ASP.NET Web Forms (.NET Framework 4.8, C# 7.3) file manager with a Gemini-powered website builder.

## Layout

```
HearthFileManager/
  Global.asax.cs            routing switch (pageless)
  engine/                   HttpContext-free core (testable from PowerShell)
    AppConfig.cs            /App_Data/config.json, users, PBKDF2 hashing
    AppSession.cs           "hfm" cookie → RAM → App_Data/sessions.json
    FsService.cs            sandboxed file ops on App_Data/wwwroot (zip, recycle bin, chunked upload, search)
    Ai/GeminiAgent.cs       Gemini REST + function-calling loop, rate limiter, model fallback
    Ai/AiTools.cs           tools exposed to Gemini (list/search/read/write/replace/mkdir/move/delete/sqlite)
    Ai/AiUndo.cs            per-turn snapshots in App_Data/ai-undo (last 50 kept)
    Ai/AiTaskManager.cs     background AI turns + polling
  RH/                       one handler per file: XxxPage (HTML) + XxxPageApi (JSON)
  js/site.js                Api (the only fetch/XHR layer), Toast, Dialog, Fmt
  js/components/            hearth-editor.js (HearthEditor.open(path)), hearth-store.js (IndexedDB)
  assets/fontawesome/       self-hosted Font Awesome
App_Data/
  config.json               users, Gemini key/model/fallbacks/rpm, upload limit, site URL, DevAutoLogin
  wwwroot/www               → public website (point the second IIS site here)
  wwwroot/db                → SQLite files for the PHP site (not publicly served)
  wwwroot/recycle-bin       → deleted items  (index: App_Data/recycle-index.json)
tests/
  test-gemini.ps1           run one AI turn through the compiled DLL (no IIS)
  test-undo.ps1             undo the oldest AI turn in the sandbox
```

## Deploying

1. Publish/copy the site including `bin\x86\SQLite.Interop.dll` and `bin\x64\SQLite.Interop.dll`.
2. The app pool identity needs **modify** rights on `App_Data` (config, sessions, wwwroot, undo, tmp).
3. **Set `"DevAutoLogin": false`** in `App_Data/config.json` on production (it only works for `localhost` anyway).
4. Second IIS site (HTML + PHP): physical path `...\App_Data\wwwroot\www`. Its app pool identity needs
   modify rights on `wwwroot\db` if PHP writes to SQLite. PHP: `new PDO('sqlite:' . __DIR__ . '/../db/site.db')`.
5. Uploads are sent in 5 MB chunks, so `web.config` only needs 16 MB per request; the per-file limit is
   `MaxUploadMb` in config.json (default 500).
6. First login is `admin` / `admin` unless config.json already has users – change it on the Users page.

## Gemini free tier notes (measured on this key, Oct 2026)

- Free daily quota is **per model** and small (e.g. 20 requests/day for `gemini-3.5-flash`).
  One builder request typically uses 2–6 API requests. When a model's quota is used up the agent
  switches to `GeminiFallbackModels` automatically; quotas reset at midnight US Pacific time.
- Google's servers often return 503 "high demand" or hang; the agent retries with back-off and a
  150 s per-request timeout.
- Free-tier prompts may be used by Google to improve its products.

## Testing the AI engine without IIS

```
powershell -ExecutionPolicy Bypass -File tests\test-gemini.ps1 -Prompt "write a simple under maintenance page" [-Model gemini-flash-latest]
```
Uses the key/model from the project's `App_Data/config.json`, works on `tests/sandbox/App_Data`, and keeps
the conversation in `tests/sandbox/history.json` (delete it to start fresh).
