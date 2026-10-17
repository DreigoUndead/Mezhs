# Normal MEZS Chat attachments - Task 4A, 2026-10-17

## Scope

Branch: `agent-model-presets`, PR #21. This task addresses the normal `/v1/files` -> `/v1/messages` -> worker -> provider flow and the shared MEZS Chat composer. The Agent Web/API and ChatGPT provider browser module were not modified. Do not merge or enable the ChatGPT Account input capability until the final real account test is completed.

## Implemented

- A user upload is limited to 20 MiB at the HTTP endpoint and while streaming into local storage. Failed uploads remove temporary file directories.
- File extensions, MIME types, and PNG structure are checked before the upload is registered. The normal Chat file picker offers TXT, MD, CSV, PDF, DOCX and PNG. JWT/login and external account files are not included in the repository.
- Attachment file IDs must belong to the message's selected connection. A rejected request does not create a new local conversation. Queued worker sends use the same ownership check.
- The UI prevents duplicate send invocation, blocks send until all selected uploads finish successfully, displays upload errors, restores unsent text on request failure, and clears pending attachments on chat/connection changes.
- Separate mock connections cover input-disabled and image-disabled capability responses.

## Checks completed

- .NET 10 Release API build: PASS (0 warnings, 0 errors).
- Normal Chat React/TypeScript bundle build: PASS (to be rerun for final changes).
- Isolated `tests/api-smoke.ps1` (port 5198, test-owned storage): PASS, including text attachment-only message, PNG upload/message, rejection of malformed image, unsupported GIF/JPEG/WebP, connection ownership, capability denial, upload limit, and cleanup.
- `node --test tests/chatgpt-account-api.test.js`: 75/75 PASS; earlier complete Node suite: 120/120 PASS.

## Live verification blocker / release gate

**Not verified:** authenticated ChatGPT Account text and PNG via the normal MEZS Chat API/UI. Earlier direct authenticated sidecar tests proved TXT marker reading and actual PNG color reading, but they bypassed the normal UI/API. A separate API+Electron run requires an authorized profile. `D:/Projects/Mezhs/data/connections/chatgpt-sub/profile/Network/Cookies` was held with an exclusive lock by an active Electron process. A disposable profile clone in ignored `tests/data/account-e2e/` could not copy it, including a read-only attempt; the clone is unusable. No existing Electron process was stopped, no original profile was changed and no live test prompt was submitted. Ports 5060 and 5174 were occupied by active Agent services and were not touched.

**Gate remains closed:** `ChatGptAccountIntegration.Capabilities.FileInput` and `ImageInput` remain `false`. It would be misleading to present this as enabled. When an authorized isolated test profile can safely be made available, enable those values on a disposable test build; verify a file-specific marker and a distinct PNG color answer through `/v1/files` and `/v1/messages`, with one local user message and reply, complete remote conversation ID, no duplicate provider turn, and correct persisted attachment history. Verify a follow-up with an attachment as well. Enable and commit production capability values only after the test succeeds.

The attempted isolated profile copy is test-only, ignored by Git, and must never be added to a commit or diagnostic report. No secrets or browser profile contents belong in the branch.
