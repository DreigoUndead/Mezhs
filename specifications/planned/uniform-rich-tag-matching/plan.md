# Uniform position-independent tag matching

ID: uniform-rich-tag-matching
Branch: `uniform-rich-tag-matching`
Level: **High-risk** — changes host-shell command discovery and the common UI/Agent parser.
State: **PLANNED — independent plan review required before implementation**
Owner direction (2026-11-17): No opening/closing tag depends on line boundaries or other position-specific exceptions. On seeing a recognized paired opening tag (for example `<SH>`), search for the corresponding closing tag anywhere after it. Without a matching closer, the opener is ordinary text and must not trigger a command or poison other otherwise valid tags.

## Goal and scope

Apply one lexical matching rule to all paired MEŽS tags (Agent blocks and rich UI). Both `<SH>cmd</SH>` and multiline `<SH>\ncmd\n</SH>` must be recognized, including embedded occurrences in prose. A missing `</SH>` is not an Agent command and is not a parse error by itself. The same applies to `ATTACH` and paired rich UI controls.

Preserve semantic distinctions of existing tag forms: leaf/self-closing UI tags and the bodyless `DONE` marker do not require a closing tag; balanced nested UI containers continue to match the appropriate nested closing tag; `SH` and `ATTACH` bodies remain opaque and end at their first matching closing delimiter. Preserve literal command-body bytes (including spaces, newlines, Unicode), rather than line-normalizing them or trimming a leading/trailing newline implicitly.

## Approach

1. Keep `Mezhs.Common.RichUi.RichDocumentParser` as the **single owner**. Change common tokenizer/matching and opaque-span discovery rather than adding a second Agent-specific scanner. Remove line placement gates (`Standalone`, `WhitespaceLine`, and line-delimited closing restrictions) from recognition of all paired tags. Remove contextual recognition exceptions for commands in Markdown, UI scopes, raw editors and `escape`, as directed; an opening paired command tag is scanned wherever present. Do not hide malformed/unclosed candidate openers or consume later independent complete tags.
2. A complete recognized pair becomes its corresponding typed node or command candidate. An unmatched opener stays inert source text and does not make the entire reply invalid. Ensure display DTO / renderer and command parser consume the same source spans. A matched-but-invalid command is still subject to existing syntax/policy validation and whole-batch rejection; never execute a partial batch after a rejected *complete* command.
3. In `Mezhs.Agent.Api/Commands/Parser.cs`, remove exactly the matched tag span from visible content, not the containing lines or adjacent prose. Preserve all surrounding whitespace/content. Do not create a new command interpreter or bypass `Registry`, selected Agent policy, `Executor`, `DONE` order, size/resource bounds or existing command result recording.
4. Update generated model instructions (`PolicyDecoder`, `instructions.yaml` as appropriate), `docs/agent-tools.md`, `docs/supported-rich-ui.md`, and `specifications/current/agent-runtime.md` to remove the standalone-line rule and obsolete context exceptions. Avoid duplicate competing instructions.
5. Update canonical parser, Agent command interpreter and UI rendering test suites to test the same grammar. Keep existing multi-line commands working; revise tests that intentionally asserted the superseded context restrictions.

## Security consequence — mandatory independent review

Under the owner's literal **anywhere** rule, a complete `<SH>...</SH>` in a Markdown code fence, quote, editor body, escaped example or presentational element can become a shell command if policy permits it. This intentionally replaces the previously approved inert-example/context-based protection. The independent reviewer must verify the exact security boundary and that no host actions originate from mere UI rendering, user-authored messages, incomplete blocks, or denied Agent policies. Docs should state this consequence clearly. No workaround should silently reintroduce tag-position/context exceptions.

## Acceptance criteria

- [ ] Same-line, multiline, mid-paragraph and mixed adjacent `SH`/`ATTACH`/paired UI tags all match; no line or indentation sensitivity.
- [ ] Matching is consistent in Markdown contexts, containers, raw editors and escape; no context exemptions or parallel parsing rules.
- [ ] Paired opening tag without closing tag is ordinary text, not a rejected command or parser failure by itself; later valid pairs still work.
- [ ] Nested same-name UI containers close correctly; opaque command bodies do not recursively execute embedded tags.
- [ ] Displayed text loses only spans consumed as commands, preserving adjacent characters and line breaks.
- [ ] Unchanged allowed/denied policy, `DONE` constraints and full-batch validation continue to apply to complete commands.
- [ ] Rich UI rendering on Agent and ordinary Web, plus persisted/reloaded messages and inert streaming previews, retain safe normalized typed output.
- [ ] Exact previous failing example `<SH> cd /d D:\\Projects\\eflex-framework && git status --short --branch && git branch --show-current && type specifications\\planned\\debug-log-files\\plan.md && if exist AGENTS.md type AGENTS.md </SH>` is recognized with body bytes as authored.

## Verification and workflow

- High-risk independent plan review **before coding**; then development on this branch, independent code review and independent final testing before any owner-approved merge.
- Developer checks: `tests/Mezhs.RichUi.TestApp`, `tests/agent-protocol-result.ps1`, `tests/policy-decoder.ps1`, JS/SSR rich UI rendering and real Chromium smoke, relevant API/Executor checks, .NET Release builds and GitHub Actions at pinned commit.
- Independent tester exercises harmless same-line and multiline shell/attachment commands and checks durable execution records, plus negative tests (unclosed commands create no shell records; denied policies cannot execute).
- Move this plan to `specifications/implemented/uniform-rich-tag-matching/plan.md` before code review. No release version/changelog changes on this branch. Do not merge without separate explicit owner approval.
