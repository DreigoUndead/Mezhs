# MEŽS

Environment-specific instructions for agents executing the
development workflow inside MEŽS.

## Agent creation

When a new agent is required, create it through the local
MEŽS Agent API instead of asking the owner to start it.

1. Read the Agent API address from `agent.yaml`
   (`server.listen`).
2. Get the current execution's `policyId` using
   `GET /v1/executions/{executionId}`.
3. Call `POST /v1/executions` with:

   ```json
   {
     "policyId": "<current-policy>",
     "input": "<next-role-prompt>"
   }
   ```

   Omit `chatId` to create a fresh chat.
4. Verify the returned execution ID using
   `GET /v1/executions/{executionId}`.

If creation fails or its outcome is uncertain, inspect
the server state before retrying.

Use a manual handoff only when direct creation is unavailable.

## Isolated live testing

When live tests require running a separate MEŽS instance:

- Use a temporary worktree at the revision being tested.
- Use separate API and web ports, storage and databases.
- Use separate Electron browser profiles.
- Start with explicit test configuration paths, without
  changing the owner's active configuration.
- Authenticate test profiles separately when required.
  Never directly copy an in-use Chromium profile. An explicitly
  owner-authorized VSS snapshot clone must follow [Testing](testing.md).
- Stop test processes and remove temporary resources
  afterward.

Never run the test instance against the owner's active
application storage or browser profile.

## Release reference documentation

During Deploy, update `docs/agent-tools.md` and
`docs/supported-rich-ui.md` when their documented behavior
changed. Base the updates on the final implementation,
not planned or unmerged behavior.
