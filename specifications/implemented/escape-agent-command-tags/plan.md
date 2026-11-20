# Agent command tags inside escape

ID: escape-agent-command-tags  
Branch: `escape-agent-command-tags`  
Level: **Small — explicit owner override (2026-11-20)**  
State: **Simplified on task branch; pending CI and owner review/merge approval**

## Owner decision and scope
At document root, `escape` discovers only *complete* registered Agent commands/markers whose definitions set `AllowAsChild=false` (currently SH, ATTACH and DONE). Incomplete delimiters, including an opener inside one escape and a closer inside another, remain literal. Do not reconstruct commands across wrappers. Remaining escape content may be displayed in gray for visual inspection; it never gains UI or Agent authority. Nested escape within a complete rich container remains inert.

## Implementation
Keep the existing canonical `ReadChildren` traversal, `TryTag` tokenizer and `FindContainerClose` / `FindOpaqueClosing` matching. Register one optional handler for the `escape` tag; its small routine scans that bounded body using those existing primitives, skips other elements and collects only eligible commands through the same `ReadCommand` path as ordinary root tags. The Agent Registry, Interpreter and policy checks remain unchanged. Do not introduce a second grammar, cross-wrapper pairing, a generalized first-level tag finder or another control framework. One complete command occupying the entire escape can consume the whole wrapper span so inline Markdown flow remains unchanged.

## Verification
- Canonical parser: complete escaped SH/ATTACH/DONE, exact command bodies/spans, mixed inert UI, gray literal content, nested escape, unclosed tags, and DONE ordering.
- Explicit negative checks: split escape wrappers are literal and do not execute, nested known/unknown/SVG/raw content is not promoted, and malformed command batches fail closed.
- Agent parser/Executor: a complete escaped shell command runs once; escaped DONE works; attachment staging and ordinary root commands remain unchanged.
- CI/build and browser checks must be verified at the final commit. No independent agent will be spawned: **the owner asked to review directly**. No merge, VERSION or CHANGELOG change before separate owner approval.
