# TODO: MEŽS agent execution UX, reliability, and efficiency

**Handoff date:** 2026-10-14
**Status:** assessment and proposed work only; **nothing in this checklist has been implemented by this handoff**.
**Basis:** one unusually long MEŽS agent session involving anonymous ChatGPT debugging, local browser probes, TypeScript changes, Node tests, .NET build, Git commits/pushes, and `.agents` documentation. It is a qualitative case study, **not** an instrumented performance benchmark.
**Related:** ChatGPT Account and guest incident handoff at .agents/chatgpt-private-api-debugging.md on agent-model-presets; review branch `agent-model-presets`, PR #21.

## Summary

The agent rated MEŽS approximately **7.5/10 overall** (subjective): task completion **9/10**, command execution **8/10**, reliability **7/10**, speed/efficiency **5/10**, developer experience **7/10**. MEŽS already performs substantial autonomous development work: commands, tests, source edits, debugging, commits, pushes, and continuity across many model turns.

**Main finding:** the work generally completed, but the interaction between agent and host was inefficient and sometimes ambiguous. Do not assume every delay is an executor bug: a large fraction of the overhead came from the agent's own sequential inspection/edit/test decisions, and the intermittent third-party guest integration genuinely required experimental diagnosis. No trustworthy per-stage timing breakdown was collected.

## Confirmed observations from this session

1. **End-to-end work succeeded.** Anonymous guest failure handling was changed in commit `0f561ac` and integration findings documented in `4a6b1f6`. A later handoff commit extended `.agents`. **61/61 JavaScript tests** passed; the ChatGPT integration built with **0 warnings / 0 errors** using an isolated output directory. A working live guest conversation was **not** demonstrated after `force_login: true` began appearing.
2. **Long serial interaction chain.** The implementation and incident handoff involved many consecutive shell-result/model cycles, including repeated inspection, edits, tests, review, and Git operations. This count was a qualitative impression, not an exact benchmark. The Node suite itself took around **1 second** and the isolated .NET integration build about **3 seconds**; neither accounts for the whole wall-clock duration.
3. **Unsafe compound-command success interpretation (execution `824`).** A Python patch attempt emitted a `SyntaxError`, then subsequent JavaScript tests passed. The host marked the **shell block** successful (`succeeded: true`, final exit code 0) even though an earlier constituent command failed. This is consistent with the documented `cmd.exe`/compound-command contract, **not evidence the executor incorrectly read an exit code**. It remains a hazardous API for unattended work: a model can mistake successful validation of *unchanged files* for successful editing.
4. **Windows `cmd.exe` quoting/escaping failures (executions `822` and `824`).** Multiline `node -e`/Python editing payloads broke under shell parsing. The agent spent additional turns repairing tooling mistakes unrelated to the feature. The handoff used encoded single-line scripts because no dedicated local patch tool was exposed; this works but is awkward and hard to audit.
5. **Structured response markup was rejected as a command.** During the long project review, MEŽS returned `Malformed command tag '<box border radius="lg" padding={3} gap={2}>'`. The `<box>` element was UI/presentation text, **not a host command**. An additional presentation response containing `<escape><DONE></escape>` had to be carefully separated from actual standalone `<DONE>` completion. Investigate the parser/transport boundary: recognized command tags should be strict, and ordinary response formatting must not be treated as malformed execution requests.
6. **Noisy build due to locked output files (execution `828`).** An in-place solution build repeatedly retried copying executables/DLLs locked by already-running MEŽS/Visual Studio processes. Output was large/truncated. A later isolated-output build (execution `829`) passed immediately. Do not kill a user's applications just to obtain a successful build.
7. **Tests are narrower than live integration evidence.** Guest mock regressions passed, but upstream private endpoints are undocumented, and the original completed-but-empty response was **not** causally explained. The observed `force_login: true` case was a **different no-submit failure**. Tests must not be described as proving more than they do.
8. **Durable execution IDs and separate Agent/Executor results were helpful.** They enabled recovery and differentiated successful shell side effects from failures during later model follow-up. Preserve that contract rather than introducing an unrelated retry system.
9. **Unrelated working-tree material exists.** `.codegraph/` was untracked during this review and intentionally left untouched; future work must not delete/add it as part of this TODO.

## Priority 1: Make failures unambiguous

### A. Compound command exit status (P1, safety)

**Problem:** For `cmd.exe`, a multi-command SH block reports the last command's exit status. In execution 824 the patch failed with `SyntaxError`, later tests succeeded, and the SH outcome was reported as success. This matches the current contract; the dangerous part is the agent's interpretation.

**Next chat:** Inspect `src/Mezhs.Executor/ExecutorRunner.cs`, the Agent `Shell` command/result contract, and current tests before designing anything. Decide whether separate SH blocks, an explicit fail-fast option, or a structured per-command result is warranted. Do not pretend that `cmd.exe` natively exposes individual inner-command exit codes.

**Acceptance:** Reproduce failed edit -> successful test; the failure is visible and cannot be confused with an applied change. Require a post-edit verification (`git diff` or hash) before claiming files changed. Preserve execution IDs, cancellation, and existing single-command behavior.

### B. Distinguish ordinary presentation markup from commands (P1, correctness)

**Problem:** The reply contained a harmless `<box ...>` UI element, and MEŽS rejected it as `Malformed command tag`. This is not an attempted shell command; it indicates a formatting/parser-boundary problem worth reproducing.

**Next chat:** Inspect `src/Mezhs.Agent.Api/Commands/Parser.cs`, Agent Worker and transport rendering. Test ordinary markup, unknown tags, exact standalone `<SH>` / `<DONE>`, escaped completion examples, and untrusted command-shaped content. Preserve strict authorization for real shell execution; do not fix the problem by executing loose or malformed tags.

**Acceptance:** User-visible formatting is inert; valid command directives still work; malformed intended commands fail safely with useful errors. Add regression tests for the actual `<box>` incident.

## Priority 2: Reduce unnecessary work

### C. Structured patch/edit operation (P2, developer experience)

**Problem:** Two source-edit attempts (822, 824) failed under Windows `cmd.exe` quoting, forcing awkward encoded one-liners. File-editing through a shell is not a good default.

**Next chat:** First inspect existing host tools and local bridge support; do not build a second file editor if a suitable one exists. Prefer a bounded `ReadFile` and exact-context `ApplyPatch` operation; only add file creation/replacement as needed. Keep workspace authorization, path traversal protection, CRLF/encoding preservation, and conflict detection.

**Acceptance:** Apply a multiline change with quotes/backticks/braces, report changed files, reject mismatched hunks without partial edits, and verify against the resulting Git diff. Include tests for workspace boundaries, line endings and stale file content.

### D. Fewer model/tool round trips (P2, efficiency)

**Problem:** Repeated read/edit/test/re-read cycles made fast commands feel slow. Many loops resulted from agent planning rather than the executor.

**Next chat:** Look at current Agent instructions and result sizes. Batch *independent read-only* inspection; keep dependent edits and side effects sequential. Prefer targeted tests during development and one broad final validation. Avoid heavy, repetitive output when a concise result with retrievable full logs will do.

**Acceptance:** Compare repeatable workflows before/after using model turns, SH blocks, failed edits, tests, and total elapsed time. Do not blindly batch or replay shell side effects.

### E. Safe validation while MEŽS is running (P2, reliability)

**Problem:** In execution 828, a full solution build retried DLL/EXE copies locked by running MEŽS/Visual Studio, creating lengthy truncated output. In execution 829, an isolated-output ChatGPT integration build passed quickly.

**Next chat:** Establish a documented non-destructive build/check recipe with isolated output directories when appropriate. Keep regular builds available for developers. Do not kill user processes just to build.

**Acceptance:** Clearly state whether *one project* or *the full solution* was validated; make file-lock warnings easy to recognize, retain complete logs when needed, and avoid endless retries.

### F. Per-stage timing and failure metrics (P2, diagnostics)

**Problem:** The review's 7.5/10 rating and perceived slowness were subjective. We saw roughly one-second Node tests and a three-second isolated .NET build, but no breakdown of model latency, queueing, shell duration, waits or browser calls.

**Next chat:** Reuse durable Agent/Executor execution IDs. Measure request/response time, queue wait, command duration, retry count, result bytes/truncation, stage errors, and model/tool turn counts. Avoid secret-bearing payload logs or double-counting concurrent durations.

**Acceptance:** A single task timeline separates model, host and external-service waits; collect a baseline before deciding which optimizations matter.

## Priority 3: Preserve recovery correctness (P3)

MEŽS already exposes useful durable command execution evidence. Keep side effects distinct from subsequent model-response failures. Never blindly resubmit an uncertain accepted prompt or rerun a successful shell command.

The guest `empty_guest_reply` (conversation started but response unreadable), `guest_login_required` (`force_login: true`, no retry), and `guest_submission_timeout` (no detected conversation) are different states. See `chatgpt-private-api-debugging.md`. The tests validate the implementation paths but do **not** prove the original upstream empty-response cause or restore guest access.

## Next-chat starting instructions

1. Read `.agents/README.md`, this TODO, and the `repository-change-workflow`, `software-change`, and `systematic-debugging` skills. Inspect the current branch and working tree first. Do not touch unrelated `.codegraph/`.
2. **Measure and reproduce** an actual failure before editing. Do the two P1 issues first; treat them as separable tasks if they require independent design/review. Confirm existing primitives before implementing any file-edit abstraction.
3. Implement the smallest coherent fix with deterministic regression tests, not speculative fallback chains. Verify the exact failed-edit/green-test sequence and markup rejection.
4. Run targeted Node tests, appropriate .NET validation, `git diff --check`, and a harmless live Agent command/result continuation. Record what each check *does and does not prove*.
5. Follow the repository review workflow: commit and present a focused PR for review; **do not merge without explicit post-review approval**.

**Definition of done for this handoff:** The next agent can investigate and implement one of these priorities without reconstructing the long conversation. This is a backlog and assessment, **not authorization to implement every item at once**.
