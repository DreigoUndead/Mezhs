# MEŽS Agent Handoff

For GitHub operations, prefer the internal GitHub tool when it is available for the task.

When the development workflow requires a new agent, start it directly through the MEŽS Agent API using `SH`. Do not give the owner a prompt to copy manually.

1. Prepare the handoff prompt according to the existing workflow.
2. Read the current `policyId` from `GET /v1/executions/{executionId}`.
3. Use the Agent API address configured in `agent.yaml` (`server.listen`).
4. Create the agent with `POST /v1/executions`, sending:
   ```json
   {"policyId":"<current-policy>","input":"<handoff-prompt>"}
   ```
   Omit `chatId` to create a new chat.
5. Verify creation using the returned `executionId` and `GET /v1/executions/{executionId}`. Report the new chat ID when available.

If creation fails or its outcome is uncertain, report the issue instead of blindly retrying. Fall back to a manual handoff only when direct creation is unavailable.

All workflow stages, independence requirements and owner approval rules remain unchanged.
