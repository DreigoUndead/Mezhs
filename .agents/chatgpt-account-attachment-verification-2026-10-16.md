# ChatGPT Account attachment uploads: full implementation and live verification report

Date: 2026-10-16
Repository: `D:\Projects\Mezhs`
Review branch: `agent-model-presets`
PR: https://github.com/DreigoUndead/Mezhs/pull/21
Status: Verified live for a text file and an opaque PNG; public file/image input capability flags remain disabled. Keep review branch unmerged pending explicit approval.

## Executive summary

The authenticated ChatGPT Account adapter can now upload an attachment, confirm server-side file processing, prepare a conversation request, submit exactly one semantic user turn, and receive a completed answer. Live validation included (1) a text document whose distinctive marker was reproduced exactly and (2) a pixel-verified 256x256 PNG whose magenta upper-left and lime-green lower-right corners were identified correctly by the model. The PNG test yielded a conversation ID and parent message ID. These successes demonstrate real functionality for these fixtures, not general support for all MIME types, sizes, accounts, or future private-API versions.

The work changes `integrations/Mezhs.Integrations.ChatGpt/browser/chatgpt.ts` and `tests/chatgpt-account-api.test.js`; related prior investigation is in `.agents/chatgpt-private-api-debugging.md`. This report is the final attachment-specific handoff. The fix preserves the authenticated semantic API; it does not introduce DOM clicking for account messages.

## Pipeline

1. Read attachment bytes from the local authorized path.
2. Call `POST /backend-api/files` with filename, size, MIME type, and `use_case` (`multimodal` for images; `my_files` for documents).
3. Transfer bytes to the supplied HTTPS signed destination via PUT. Supply required Azure blob headers; reject unsafe schemes, unsupported multipart and Estuary paths, and failed transfer responses. Do not log signed URLs or private bodies.
4. Call `POST /backend-api/files/process_upload_stream`; consume both SSE-style `data:` frames and bare newline-separated JSON objects as observed live. Match file IDs and require an explicit processing-completed/file-ready event; stop on failure or incomplete processing.
5. Build message attachments as metadata and image parts as `image_asset_pointer` objects carrying file-service pointer, byte count, width and height. For PNGs, extract the dimensions from the validated signature/IHDR; zero dimensions and unsupported image MIME types fail explicitly.
6. Use a text-only `partial_query.content` for `POST /backend-api/f/conversation/prepare`; the actual `POST /backend-api/f/conversation` message retains the multimodal image part. Keep normal requirements/Sentinel handling and only one semantic conversation submission.
7. Poll provider state for the completed assistant turn and return reply, conversation ID and parent message ID.

## Failure sequence, evidence, and fixes

**Authorization blocker:** An earlier live attempt failed before reaching file upload because the local sidecar account session lacked a usable access token, despite the session endpoint reporting HTTP 200. This was an authorization blocker, not evidence about upload correctness. After launching the authenticated profile through the correct module, subsequent live API tests reached upload endpoints. Also ensure the extracted runtime module hash equals the rebuilt resource/source hash; an old sidecar can silently run old code.

**Blob transfer:** Initial live signed uploads needed required blob headers for custom Azure destinations. The upload implementation includes `x-ms-blob-type`, `x-ms-version` and server-provided upload headers; it rejects protocols it has not actually implemented. A fix that lets PUT succeed does not alone prove the cause of earlier downstream failures.

**Processing stream:** The live `/files/process_upload_stream` completion was bare newline-delimited JSON, even though content-type could suggest an SSE stream. The old parser missed readiness and reported an incomplete upload. Commit `4b18da8` recognizes this shape and requires the matching file ID/completion. Test coverage includes incomplete and failed streams, unexpected upload strategies and transfer errors.

**Document proof:** A real text attachment was uploaded and ChatGPT returned its distinctive in-file marker exactly; the returned conversation/parent IDs were populated. This verified document semantics, not only upload acceptance.

**Image prepare HTTP 422:** The first image conversation failed at `/backend-api/f/conversation/prepare`, whose validation rejected an `image_asset_pointer` object in `partial_query.content.multimodal_text.parts`. The correct partial query carries `content_type: "text"` with string parts only, without stripping the image from the actual conversation payload. This path is covered by a regression.

**Image conversation HTTP 422:** After the prepare fix, the request reached `/backend-api/f/conversation` and failed validation on the image part. Comparing the outgoing object with known message shapes showed that `width` and `height` were missing. The adapter now extracts actual PNG IHDR dimensions and supplies these fields in the image part and attachment metadata. A targeted regression failed before this change and passed afterward. The accepted live request supports this protocol-mismatch diagnosis; it does not independently prove every upstream validation rule.

**Misleading first visual test:** An earlier generated PNG was *not* the intended two-color fixture: its top-left was magenta, an upper region was orange, and the lower-right was transparent black. A completed response of "Orange / Black" therefore could not validate the intended magenta/lime claim. Inspecting pixels corrected the test setup.

**Decisive visual test:** A replacement opaque 256x256 PNG was created and sampled. Upper-left RGB=255,0,255 alpha=255; lower-right RGB=0,255,0 alpha=255. The active authenticated sidecar completed the image conversation and ChatGPT answered **"Magenta and lime green."** The operation returned both conversation ID and parent message ID. This verifies that the model could access image content in that tested path.

## Validation evidence

- `node --test tests/*.test.js`: 79/79 passed, including image/document request sequencing, existing-conversation attachment regression, failure paths, anonymous guest and other integration regressions.
- `dotnet build src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj -c Debug -o <temporary staging directory> --nologo -v quiet`: succeeded with zero warnings and zero errors.
- `git diff --check`: clean for the code/test changes.
- New staged API started successfully and responded HTTP 200 on `127.0.0.1:5060`. The existing UI responded HTTP 200 on `127.0.0.1:5174`.
- The active sidecar extracted ChatGPT module SHA256 equaled the changed source SHA256, ruling out stale embedded code for the final test.
- The real PNG test was executed using `newChat` through the sidecar `/invoke` operation, rather than a mocked unit test.

## Constraints and known gaps

- `ChatGptAccountIntegration.Capabilities` still advertises `FileInput: false` and `ImageInput: false`. The broader MEZHS UI/agent input path is not enabled merely because the lower-level account adapter can handle files.
- PNG is the only image format whose dimensions this change currently extracts. JPEG/WebP/GIF/etc. are not implemented for image submission.
- Unsupported multipart/Estuary upload strategies fail rather than falling back. The current implementation does not claim arbitrary large-file support.
- Only the text-document and PNG fixtures above have live, content-based end-to-end proof. The existing-conversation attachment behavior has a unit regression; its independent live result was not established in this final verification.
- Private ChatGPT endpoints and challenge/requirements behavior are undocumented and can change. Never convert an upstream 200 or a completed conversation alone into proof that the attachment was semantically available; verify an answer dependent on its contents.
- Do not log authentication cookies, access/Sentinel tokens, signed destinations, full request or response bodies, document contents, or personal image bytes.

## Debugging/next steps

When this breaks, locate the earliest failing stage: session authorization -> `/files` creation -> signed PUT -> processing stream file-ready event -> `/f/conversation/prepare` -> `/f/conversation` -> conversation state/semantic answer. Distinguish HTTP response status, accepted payload shape, and actual content understanding. Use bounded, sanitized observations (paths, status codes, recognized event/type/key names, IDs compared for match without dumping the IDs). For image tests, validate actual pixel values and opacity rather than trusting the image creation prompt.

Before advertising input capabilities, add supported image formats and invalid-image regressions, verify agent/UI attachment plumbing end to end, cover a live existing-conversation attachment, assess size limits and provider changes, and review the error messages for diagnostic noise. Retain the one-review-branch/one-PR policy; do not merge without the user's explicit post-review approval.

## Commit and review policy

The earlier branch commits contain the upload implementation and protocol investigations; this follow-up adds the text-only prepare payload, PNG image dimensions, and regressions. The unrelated untracked `.codegraph/` cache and `eFlex` were intentionally excluded. The full branch should be reviewed as a single PR against `main` and left unmerged until approved.
