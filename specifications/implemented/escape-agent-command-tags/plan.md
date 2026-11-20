# Agent command tags inside escape

ID: escape-agent-command-tags
Branch: `escape-agent-command-tags`
Level: **Small — explicit owner override (2026-11-20)**. The original plan classified the command-discovery change as high-risk; the owner explicitly chose Small and the narrow behavior below.
State: **Implemented on task branch; pending final CI and owner merge approval**

## Goal and scope
A model sometimes wraps an Agent command opener and closer in separate `escape` elements. Those command tags should work, rather than silently become inert.

At **document root**, `escape` recognizes the registered Agent protocol tags with `RichTagDefinition.AllowAsChild == false` (currently `SH`, `ATTACH`, `DONE`). All other tags and ordinary content inside `escape` remain literal/inert; `escape` does not become a general UI unescaper. An `escape` nested inside another complete UI container does not authorize commands.

## Behavior
- Recognize a complete Agent command inside root `escape`; recognize the observed split form, in which one `escape` wraps only the opening delimiter and a second wraps only the matching closing delimiter. Preserve the exact shell/attachment body between delimiters, including whitespace.
- Only tags with `AllowAsChild == false` that also pass the existing Agent `Registry` can become actions. Other markup inside root `escape` is ignored for command discovery, not converted into UI or executable behavior.
- Preserve current ordinary-root-command behavior, including an unmatched `escape` opener being treated as plain text. Do not invent a partial command from unmatched `SH`/`ATTACH` delimiters.
- Keep existing Agent policy checks, `DONE` ordering, whole-batch validation, attachment staging, and shell execution unchanged. Display projection should remove recognized command source while retaining unrelated visible text.

## Implementation
- Make the narrow exception in the canonical `RichDocumentParser` using its existing tag tokenizer, registry metadata, and command spans. Reuse the existing Agent `Parser` and `Interpreter`; no second parser/scanner or new authorization mechanism.
- Document the specific exception in developer-facing command docs; keep the Agent prompt generic as the owner requested. Do not change VERSION/CHANGELOG before release.

## Verification
- Parser tests: normal root commands unchanged; complete escaped `SH`/`ATTACH`/`DONE`, split escaped delimiters, exact body bytes, visible text, and source spans.
- Negative tests: other `escape` markup inert; commands in outer `box`, unknown UI, raw editors, or SVG inert; unmatched command delimiters do not become actions; existing unmatched `escape` behavior retained.
- Agent integration: split-wrapped shell command executes once, attachment stages once, policy denial and `DONE` rules unchanged, malformed command batch executes nothing.

## Implementation notes
- Canonical parser discovers root escaped protocol tags and split wrappers without changing Agent Registry, policy or executor ownership. `RichTagDefinition.Handle` registers per-tag functions: `SH`/`ATTACH`/`DONE` use `TagHandlers.Command`; `escape` uses `TagHandlers.Escape`, which discovers eligible nested tags by calling the same traversal and registered command handlers. The generic parser has no escape-specific case, nor a hardcoded escape name.
- The tag-handler functions are grouped in `RichDocumentParser.Tags.cs` (a partial class to share private parsing primitives without a second tokenizer). Presentation tags continue to use the existing declarative role/content model. No class-per-tag hierarchy or duplicate parser is added.
- Detailed escape guidance removed from `instructions.yaml` and the authoritative Agent prompt.
- Regression coverage added to the rich parser, Agent parser/Executor smoke, and runtime instruction tests.
- Windows CI result for final commit must be verified before release; no claim of independent testing under the Small owner override.

## Workflow
Owner explicitly overrides the original High-risk classification to **Small**. Use the existing task branch. Perform targeted checks and report evidence; independent review/testing are optional for Small. A merge into `main` still requires separate explicit owner approval.
