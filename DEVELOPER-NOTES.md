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

## Deploy API (/api/v1) for scripts and AI agents

- Users with the **Use API access tokens** permission (`Perm.Api`) create tokens on **My Account**. A token is
  shown once; `App_Data/api-tokens.json` keeps only its SHA-256 (`engine/ApiTokens.cs`).
- `RH/ApiV1.cs` handles `whoami`, `list`, `upload`, `delete`, `help`. It is routed in `Global.asax.cs` *before*
  the cookie/DevAutoLogin code: the only credential it accepts is `Authorization: Bearer hfm_...`.
- A token acts as its user (`FsService.ForUser`): same root folder. Uploading or deleting also needs a
  read & write token and the user's **Manage files** permission.
- Upload = a zip in the request body, extracted with `FsService.UnzipFile` (zip-slip safe, replaced files go to
  the recycle bin, total size capped by `ApiMaxExtractMb`). Zips larger than one request (16 MB in web.config)
  are sent in parts with `uploadId`/`index`/`total`.
- `list` takes `exclude=uploads,storage/receipts` for folders that only matter on the live server (customer
  uploads, receipts). No slash = a folder name at any depth; with a slash = a path from the root; `*` is a
  wildcard. Excluded folders are returned once with `Excluded: true` and are not entered or hashed.
- API deletes always go to the recycle bin; the API cannot read or empty it.
- Every call is appended to `App_Data/api-log.jsonl` (rotated at 5 MB).
- `GET /api/v1/help` (and the "Copy instructions for AI" button) returns the full guide for an agent.
- `tools/hearth-deploy.ps1` is a ready-made client: compare by hash, zip the changes, upload, verify.
- Tests: `tests/test-api.ps1` (steps to start the sandbox site are at the top of the file).

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
