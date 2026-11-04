# Coding

Follow the [workflow](workflow.md), [software-change skill](skills/software-change/SKILL.md), and [systematic-debugging skill](skills/systematic-debugging/SKILL.md) for bugs.

- Read the approved plan from `main`.
- Create a new branch named after the plan ID.
- Find the existing owner of the behavior before adding code. Reuse existing mechanisms. Avoid unrelated changes.
- Update the plan as the implementation changes. Only the **final behavior** needs to be specified.
- Add appropriate automated tests and verify the change.
- Prepare the release candidate: final plan under `specifications/implemented/<id>/`, updated `specifications/current/`, `VERSION`, `CHANGELOG.md`, and root `README.md`.
- Commit and push the development branch. Do not merge.
- Give the next agent a testing prompt:

> Test branch `<id>` at commit `<sha>`. Follow `.agents/testing.md` and the final plan in `specifications/implemented/<id>/`. Run relevant automated and practical checks. Return PASS, FAIL or BLOCKED with evidence. If problems exist, give the developer a fix prompt. If everything passes, give the next agent a code-review prompt.