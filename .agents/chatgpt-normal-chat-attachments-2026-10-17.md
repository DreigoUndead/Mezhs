# Normal MEZS Chat attachments - Task 4A, 2026-10-17

## Scope

Branch: `agent-model-presets`, PR #21. This task addresses the normal `/v1/files` -> `/v1/messages` -> worker -> provider flow and the shared MEZS Chat composer. The Agent Web/API and ChatGPT provider browser module were not modified. The ChatGPT Account input capability was enabled only after authenticated real-account API and browser testing. No merge is part of this task.

## Implemented

- A user upload is limited to 20 MiB at the HTTP endpoint and while streaming into local storage. Failed uploads remove temporary file directories.
- File extensions, MIME types, and PNG structure are checked before the upload is registered. The normal Chat file picker offers TXT, MD, CSV, PDF, DOCX and PNG. JWT/login and external account files are not included in the repository.
- Attachment file IDs must belong to the message's selected connection. A rejected request does not create a new local conversation. Queued worker sends use the same ownership check.
- The UI prevents duplicate send invocation, blocks send until all selected uploads finish successfully, displays upload errors, restores unsent text on request failure, and clears pending attachments on chat/connection changes.
- Separate mock connections cover input-disabled and image-disabled capability responses.

## Checks completed

- .NET 10 Release API build: PASS (0 warnings, 0 errors).
- Normal Chat React/TypeScript production bundle build: PASS.
- Isolated `tests/api-smoke.ps1` (port 5198, test-owned storage): PASS, including text attachment-only message, PNG upload/message, rejection of malformed image, unsupported GIF/JPEG/WebP, connection ownership, capability denial, upload limit, and cleanup.
- `node --test tests/chatgpt-account-api.test.js`: 75/75 PASS; earlier complete Node suite: 120/120 PASS.

## Authenticated real-account verification

- An authorized login was completed using a separate disposable ChatGPT Account profile, with isolated MEZS API on port 5200 and normal MEZS Chat web app on port 5201. No original account browser profile, active Agent API (5060), or Agent Web (5174) was changed or stopped.
- Normal Chat API: a fresh conversation accepted marker.txt through /v1/files and /v1/messages and returned the exact file-specific marker MEZHS-NORMAL-CHAT-TXT-VERIFY-6C21F90A.
- Normal Chat API: existing-conversation follow-up accepted four-corners.png through the same file/message path. The authenticated provider answered Red, blue, yellow, green in the correct top-left, top-right, bottom-left, bottom-right order. The local history then contained exactly three user and three assistant messages, with no duplicate turns.
- Actual React composer in headless Chrome: attachment picker uploaded marker.txt and four-corners.png consecutively in a new conversation. Exact TXT marker and ordered four-color image answer were rendered; both user attachments appeared in history, the PNG image loaded, and the UI contained exactly two user and two assistant messages. BROWSER_UI_E2E_PASS.
- Capabilities.FileInput and Capabilities.ImageInput are now true for ChatGptAccountIntegration. This gate was opened only after the preceding content-based tests passed.

## Regression verification and test isolation

- Full Node suite: 120/120 PASS. API Release build and Web Release build: 0 errors, 0 warnings. React/TypeScript production build: PASS.
- tests/api-smoke.ps1: PASS, including upload validation, input capability rejection, cross-connection ownership, attachment history, file download, oversized upload cleanup, and conversation deletion.
- Smoke tests previously cleaned all of tests/data, which conflicted with the locked isolated account profile. The script and tests/mezhs.test.yaml now use tests/data/api-smoke exclusively. The previously blocked rerun then passed.
- The authenticated Chrome test is an independently executed browser test, not a checked-in CI test requiring real account credentials. MD, CSV, PDF and DOCX are allowed and validated at the normal Chat upload boundary, but only TXT and PNG received authenticated provider content-based verification in this task.
- All account profiles, browser automation dependencies, fixture PNG/TXT files, and logs remain in ignored tests/data. Do not add them to commits. No production merge was performed.
