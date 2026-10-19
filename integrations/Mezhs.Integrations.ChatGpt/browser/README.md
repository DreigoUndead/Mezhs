# ChatGPT browser integration

Edit the JavaScript source in `modules/`, not the generated `chatgpt.ts`.
The .NET integration embeds exactly one resource named `Mezhs.Integrations.ChatGpt.BrowserModule`.
The browser host extracts that resource as a standalone `.js` file; it never deploys sibling source modules.

## Module ownership

- `entry.js`: public ChatGPT integration descriptor and operations.
- `guest.js`: anonymous ChatGPT DOM composer and reply collection.
- `account-transport.js`: authenticated API requests, session tokens, and browser-owned Sentinel requirements.
- `account-models.js`: model picker mapping and selected model preferences.
- `account-files.js`: uploaded attachment preparation and downloaded artifacts.
- `account-messages.js`: semantic new/follow-up submission and completion orchestration.
- `account-stream.js`: streamed response metadata, progress, and execution metadata.
- `account-results.js`: conversation state polling and visible reply extraction.

## Packaging and checks

Run `node browser/build.js` from the integration directory (or invoke it by path) after changing a module.
Run `node browser/build.js --check` to reject a stale generated resource.
The .NET project regenerates the bundle before embedding it during build.
Run `node --test tests/*.test.js`, `dotnet build Mezhs.sln -c Release`, and the existing `tests/integration-resources.ps1` check from the repository root.
The packaging regression test copies the generated resource to an isolated directory and loads it without sibling modules.
