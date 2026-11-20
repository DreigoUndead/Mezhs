# Next Role Prompt

Use a minimal, neutral handoff that identifies the next
agent's role and assigned task-plan step.

## Template

    <Role> — <Step number> — <Task title>

    Repository: <repository, if needed>
    Branch: <task branch>
    Plan: <plan path>

Use the step number defined in the task plan.
The same role may appear in multiple sequential steps.

For a Small task without a written plan, use step 1
and replace the Plan field with a brief task description.

## Independence

Do not include:

- Previous agent's actions or explanations.
- Earlier findings, fixes, suspicions or recommendations.
- Suggested conclusions or areas to investigate.
- Copied requirements, decisions or verification reports.
- Extra instructions unrelated to locating the assignment.

The next agent must obtain its context from the repository
and task plan, not from the caller's narrative.
