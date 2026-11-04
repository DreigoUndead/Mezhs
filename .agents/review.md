# Independent code review

A fresh reviewing MEŽS agent, distinct from the coding agent, reviews the complete implementation diff against `main`, the **final specification**, and independent test evidence.

- Use [software-change](skills/software-change/SKILL.md) and its adversarial review reference.
- Check requirements, regressions, ownership and sources of truth, security and trust boundaries, concurrency/lifecycle, failure handling, test strength, maintainability, unrelated edits and unnecessary complexity.
- Verify `specifications/current/`, archived plan, `VERSION`, `CHANGELOG.md`, root `README.md` and PR match the candidate.
- Report severity-ranked, reproducible findings; distinguish observed defects from hypotheses. Return PASS/FAIL/BLOCKED with concrete evidence. Do not modify implementation as part of approval; failed reviews return to coding.
- On PASS prepare this **Monkey package**, with real values:

```text
Change: <id>
PR: <url>
Candidate SHA: <sha>
Release: <old> -> <new>
Delivered behavior: <short summary>
Independent testing: PASS (<evidence>)
Independent review: PASS (<evidence>)
Outstanding risks: <none or details>
Documentation: <spec/current/version/changelog/README status>
Monkey: explicitly approve or reject merging this candidate into main?
```

Monkey's explicit **post-review** approval is the only implementation merge authorization.