# Deploy

The Deploy role presents completed work for owner approval
and finalizes the approved release.

## Before approval

- Merge the latest `main` into the task branch, resolve conflicts,
  and verify the integrated revision **before** requesting owner approval.
  Do not modify `main` before approval.
- Present the task branch, PR or commit, and a summary
  of the delivered behavior.
- Report Code Review and Testing outcomes, verification
  evidence, known limitations and remaining risks.
- Propose release notes and a version change when applicable.

Request explicit owner approval to merge the completed change
into `main`.

Do not merge or modify `main` before approval.

## Finalization

After approval:

1. Confirm the implementation still matches the approved
   candidate and check the current `main` for merge conflicts.
2. Move the task plan, if present, from
   `specifications/planned/<id>/` to
   `specifications/implemented/<id>/` on the task branch.
   Ensure this archival change does not alter the implementation.
3. Merge the approved task into `main` and finalize the
   release following [Versioning](versioning.md).
4. Verify the released revision and clean up the merged
   task branch.

If the implementation changes materially after approval,
obtain renewed owner approval before merging.

If finalization fails, report the actual state rather than
claiming the release succeeded.
