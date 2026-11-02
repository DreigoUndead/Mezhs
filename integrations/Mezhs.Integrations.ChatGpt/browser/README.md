# ChatGPT browser integration

The files in `modules/` are the deployed CommonJS source (no generated bundle).
The .NET integration embeds each `modules/*.js` file under the resource prefix
`Mezhs.Integrations.ChatGpt.BrowserModule.`. The shared browser host extracts them
side by side into the connection's module directory and passes `entry.js` to Electron.
Electron uses Node's ordinary `require()` to resolve the sibling files.

## Module ownership

- `entry.js`: public ChatGPT integration descriptor and operations.
- `guest-page.js`: sandbox-compatible guest DOM operations.
- `guest.js`: anonymous ChatGPT DOM composer and reply collection.
- `account-transport.js`: authenticated API requests, session tokens, and browser-owned Sentinel requirements.
- `account-models.js`: model picker mapping and selected model preferences.
- `account-files.js`: uploaded attachment preparation and downloaded artifacts.
- `account-messages.js`: semantic new/follow-up submission and completion orchestration.
- `account-stream.js`: streamed response metadata, progress, and execution metadata.
- `account-results.js`: conversation state polling and visible reply extraction.

## Packaging and checks

Run `node --check` on each `browser/modules/*.js` file and `node --test tests/*.test.js`.
Build with `dotnet build Mezhs.sln -c Release` and run `tests/integration-resources.ps1`.
The package regression tests verify that all nine modules load from an isolated directory.
The published-module probe additionally checks the real embedded resources and extraction
without access to the source checkout.
