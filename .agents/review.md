# Code review

Use a separate agent from the developer and tester.

- Read the final plan, entire diff against `main`, and testing results.
- Check correctness, regressions, simplicity, architecture, security and maintainability. Use [software-change](skills/software-change/SKILL.md) for deeper analysis.
- Check that the final specifications, version, changelog and README match the implemented change.
- Report **PASS**, **FAIL** or **BLOCKED**. Give clear reasons and evidence.

If there are issues, give the developer a fix prompt. After changes, run **testing and review again**. Do not silently edit the implementation as the reviewer.

If everything passes, declare the change ready and request **project owner review**. Include:

```text
Branch / PR: <link>
Commit: <sha>
What changed: <short summary>
Tests: PASS <evidence>
Review: PASS <evidence>
Known limitations: <details or none>
Proposed version: <version>
Please review. May this exact implementation be merged into main?
```

**Wait for explicit approval.** A successful review does not authorize merging.