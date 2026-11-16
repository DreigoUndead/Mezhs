# Agent tools and command protocol

MEŽS Agent exposes a small built-in command protocol. The **active policy** determines which commands are allowed. Other software and connected tools depend on the current host and available integrations; do not assume a particular CLI or provider is installed.

## Executable Agent commands

- **`SH` (shell):** Put `<SH>` on its own line, the shell text on following lines, then `</SH>` on its own line. MEŽS passes that text directly to the host shell identified in the Agent prompt (for example, `cmd.exe` on Windows). Multiple blocks run in order. Observe the returned execution ID, exit code and output before reporting success. Each command has the policy-configured timeout. For detached children, redirect their output away from MEŽS.
- **`ATTACH` (files):** Put `<ATTACH>` on its own line, followed by **one existing file path per line**, and close with `</ATTACH>`. The runtime stages those files for the next ordinary Agent message; staging alone does not send a message.
- **`DONE` (completion marker):** If the active policy requires explicit completion, put `<DONE>` on its own line with no body. Completion constraints (such as required successful shell evidence) are checked independently. Do not claim completion before required command results are available.

These tags are a structured protocol, not HTML. The parser recognizes standalone uppercase command tags and rejects malformed, unknown, disallowed or inconsistent command batches. Wrap illustrative command tags in `<escape>` / `</escape>` when they must remain visible instead of executing. Code blocks and MEŽS rich UI are not substitutes for permitted commands.

## Host and connected tools

- **CLI tools:** Execute tools present on the host via allowed `SH` commands. Inspect availability and usage with the appropriate help command (for example, `git --help`, `dotnet --help`, or `node --help`), rather than assuming installation or command syntax. The prompt identifies the host operating system and shell.
- **Connected tools:** External integrations, including a GitHub connector, may be available in some Agent sessions. Use the tool exposed by the session for supported actions; do not assume it is present. For repository changes, prefer a connected GitHub tool when available.
- **Agent handoff:** For creating independent agents via the Agent API, follow [MEŽS Agent Handoff](../.agents/mezhs-agent-handoff.md), including policy-ID lookup and verification of created execution IDs.
- **Rich replies:** See [supported rich UI](supported-rich-ui.md) for bounded Markdown, layout, SVG and interaction syntax. Unsupported model-supplied markup or JavaScript is not executable.

See [Agent runtime behavior](../specifications/current/agent-runtime.md) and [architecture](architecture.md) for policy and execution ownership. Respect the commands allowed by the current Agent policy and keep execution evidence auditable.
