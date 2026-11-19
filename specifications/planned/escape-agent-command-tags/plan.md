# Explicit Agent command handling through escape

ID: escape-agent-command-tags
Branch: `escape-agent-command-tags`
Level: **High-risk** (changes command-discovery / execution authorization boundary)
State: **Plan only — independent plan review required before code**

## Goal and scope
MEŽS currently treats complete `escape` as an opaque literal. In the observed incident the agent wrote an `escape` wrapper around the opening shell delimiter and another around its closing delimiter, leaving an otherwise valid command inert. Change `escape` specifically for Agent protocol tokens `SH`, `ATTACH`, and `DONE` so they can be recognized and dispatched when escaped at document root.

No general unescaping of UI tags, executable JavaScript, or other syntax; no changes to command authorization/policy, allowed commands, execution timeout, attachments, or DONE ordering. This behavior intentionally changes the safety of examples that embed commands in `escape`: a complete root-level escaped command becomes actionable and must be documented as such.

## Behavior to support
- Root-level `escape` containing a complete permitted command, e.g. an escaped shell or attachment block, is treated as a protocol command.
- Root-level `escape` wrapping only an opening command delimiter and another wrapping only the corresponding closing delimiter must pair across the literal body. The incident used this form. The Executor body must be exactly the text between these wrappers (not the `escape` delimiters); preserve whitespace in the command body.
- Root-level `escape` containing a `DONE` marker is recognized and remains subject to final-command ordering and completion evidence.
- `escape` containing ordinary non-command tags or text is still an opaque literal. Commands inside other complete known/unknown presentation containers (including raw editors and SVG) remain inert, even if nested in `escape`.
- Keep one canonical bounded C# tag/parser authority; no Agent-only second tokenizer, global regex-based extraction, client-side execution, or bypass of active Agent policy/Registry.
- Unmatched or malformed command wrappers must never produce a partial action. If the syntax has no unambiguous command pairing, keep it inert or reject the whole batch, rather than execute a guessed body. Reject a malformed command batch before executing any commands.
- Preserve source spans and visible text projection consistently, and keep diagnostics meaningful.

## Risk and alternatives
Allowing escaped commands to execute is a deliberate change to a formerly literal-only boundary. It creates a risk that an escaped documentation example in an Agent reply can trigger an action. The narrower alternative is to keep `escape` inert and return an explicit error/correction if it contains Agent tags; that would prevent silent no-ops but would **not** execute the incident's command, so it does not meet the intended behavior. Independent plan review must challenge whether an explicit root-only carveout can be implemented without duplicated parsing or accidental expanded authority.

## Implementation direction
1. Extend only the canonical `RichDocumentParser` around its existing `escape`/command token handling; re-use the registry and the existing `Parser`/`Interpreter`. Introduce no parallel scanner.
2. Reuse existing source-span and command-body types. When the wrapper itself belongs to a recognized command, its span must encompass the full consumed markup so projection does not leave broken markup.
3. Adjust `RichTagRegistry` discovery semantics only if necessary for this targeted behavior, avoiding opening nested authorization in unrelated rich elements.
4. Update `docs/agent-tools.md`, `docs/supported-rich-ui.md`, `specifications/current/agent-runtime.md`, and authoritative prompt instructions if they conflict. Don't edit VERSION or CHANGELOG before release.

## Acceptance and verification
- Canonical-parser test: standalone root command unchanged; root escaped complete `SH`/`ATTACH` and `DONE` recognized; escaped split opening/closing delimiter form recognized with exact command body and original source spans.
- Parser/Agent tests: same text within an unrelated `box`, `text`, unknown wrapper, `CodeBlock`, `WritingBlock`, SVG remains inert; malformed and unclosed escape/command combinations do not authorize partial commands.
- Whole-batch checks: forbidden action policy still denies; final/duplicate `DONE` enforced; bad batch does not execute a valid earlier command.
- Real Agent/Executor integration: incident's split-wrapper command executes once; escaped `ATTACH` is staged once and not sent prematurely; nested inert negative case shows no shell side effect.
- Regression suite: .NET parser, Agent policy/command integration, relevant browser tests, Windows CI, plus independent code review and independent final tester.

## Workflow
High-risk: independent plan review on this branch before implementation; then code, fresh independent code review, independent test; owner merge approval remains separate. No direct main commits.
