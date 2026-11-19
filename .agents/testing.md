# Testing

The Tester independently verifies actual application behavior
when the task plan requires this role.

## Verification

- Perform live tests of the changed behavior and relevant
  regression and risk areas.
- Execute additional verification required by the task plan.
- Test in the intended environment using real application
  components and integrations where applicable.
- Record the tested revision, scenarios, actual results,
  failures and untested areas.
- Do not report PASS when required verification was not performed.

If live verification cannot be performed, report BLOCKED
unless the owner explicitly accepts the limitation.

## Corrections

When testing reveals a defect, investigate its cause and
correct it on the task branch.

Run relevant checks after corrections. Record consequential
changes in the task plan and commit and push them.

## Outcome

- **FAIL:** After correcting issues, hand off to a fresh
  Tester. Repeat until PASS.
- **PASS:** Continue to Deploy or the next required role
  defined by the plan.
- **BLOCKED:** Report unavailable verification, unresolved
  obstacles or required owner decisions.
