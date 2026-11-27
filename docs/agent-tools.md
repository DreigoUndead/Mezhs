# Agent tools and command protocol

MEŽS Agent exposes a small built-in command protocol. The **active policy** determines which commands are allowed. Other software and connected tools depend on the current host and available integrations; do not assume a particular CLI or provider is installed.

## Executable Agent commands

- **`SH` (shell):** Write `<SH>shell text</SH>` anywhere in a message. The first matching `</SH>` ends the opaque body; line breaks are optional and any whitespace inside is part of the command. MEŽS passes that text directly to the host shell identified in the Agent prompt (for example, `cmd.exe` on Windows). Multiple blocks run in order. Observe the returned execution ID, exit code and output before reporting success. Each command has the policy-configured timeout. For detached children, redirect their output away from MEŽS.
- **`ATTACH` (files):** Write `<ATTACH>file paths</ATTACH>` anywhere in a message. Its body is parsed as file paths by the attachment interpreter; the delimiters need no special line placement. The runtime stages those files for the next ordinary Agent message; staging alone does not send a message.
- **`DONE` (completion marker):** If the active policy requires explicit completion, write `<DONE>` anywhere; it has no body and must remain the final Agent command. Completion constraints (such as required successful shell evidence) are checked independently. Do not claim completion before required command results are available.

These tags are a structured protocol, not HTML. Paired tags match independently of line placement; an unclosed opener is ordinary text. Registered Agent commands are eligible at document root **or as direct children of an `escape` that is itself at document root**. Other nesting—including a nested `escape`, SVG, raw editors, malformed or unknown paired UI wrappers—keeps commands inert. A root-level `escape` renders its non-command content as muted literal text and omits only eligible command spans from that display. Complete commands in ordinary Markdown (including code fences) remain actionable at document root. Avoid putting complete command examples directly inside a root `escape`: they can execute. Registry, policy and whole-batch validation still apply.

## Host and connected tools

- **CLI tools:** Execute tools present on the host via allowed `SH` commands. Inspect availability and usage with the appropriate help command (for example, `git --help`, `dotnet --help`, or `node --help`), rather than assuming installation or command syntax. The prompt identifies the host operating system and shell.
- **Connected tools:** External integrations, including a GitHub connector, may be available in some Agent sessions. Use the tool exposed by the session for supported actions; do not assume it is present. For repository changes, prefer a connected GitHub tool when available.
- **Agent handoff:** For creating independent agents via the Agent API, follow [MEŽS](../.agents/mezhs.md), including policy-ID lookup and verification of created execution IDs.
- **Rich replies:** See [supported rich UI](supported-rich-ui.md) for bounded Markdown, layout, SVG and interaction syntax. Unsupported model-supplied markup or JavaScript is not executable.

See [Agent runtime behavior](../specifications/current/agent-runtime.md) and [architecture](architecture.md) for policy and execution ownership. Respect the commands allowed by the current Agent policy and keep execution evidence auditable.
