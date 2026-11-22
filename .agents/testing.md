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

- **FAIL:** Material corrections require a fresh independent
  Tester to verify the corrected revision. Repeat until PASS.
- **PASS:** All required verification passed on the relevant
  revision, with no unresolved blocking failures. Nonmaterial
  corrections were verified. Continue to Deploy or the next
  required role defined by the plan.
- **BLOCKED:** Report unavailable verification, unresolved
  obstacles or required owner decisions.

## Credential access for isolated live testing

- Before requesting a credential path, inspect the git-ignored `chatgpt-auth.json` at the main MEZS checkout root (default Windows checkout: `D:/Projects/Mezhs/chatgpt-auth.json`). It is a saved account-session export, not an inactive Chromium profile. Check its recorded expiration metadata without displaying tokens or cookies; the presence of an export does not prove current authorization.
- If no valid dedicated inactive test profile or usable credential export is available, request separate interactive authentication from the owner; never ask for passwords in agent transcripts.
- Create a git-ignored local shortcut in the task worktree (for example under `profiles/`) to the owner-provided credential source. A shortcut does not grant permission to open an active browser profile. Never copy, log, commit, or attach credentials or cookies.
- Sign in through the separate test browser. Request owner interaction only for actions that cannot be performed safely by the agent, such as entering credentials or completing MFA. Verify real provider message delivery after signing in.
- An active owner Chromium profile is not a reusable test profile, even if its path is known. Do not point the isolated test instance at it.

## Reusing an inactive browser profile with a Windows junction

An isolated Windows MEZS test may link to a dedicated persistent, **inactive** test-account Chromium profile rather than copy cookies or sign in on every worktree. This is a shared directory, **not** an isolated copy: the test browser can modify the target profile. Never open the same profile in two Electron/Chromium processes. An idle owner application may still have its profile open.

1. Keep the test API port, configuration, storage root, databases and other connections separate. Create the test connection parent directory, but leave its `profile` destination absent.
2. On Windows, create a directory junction using `mklink /J C:\Temp\mezhs-test\data\connections\grok-test\profile D:\Mezhs-TestProfiles\grok-account\profile`. Quote paths if they contain spaces. Use an inactive **test** profile, never an active owner browser profile.
3. Verify that the destination is a junction pointing to the intended source. Start the isolated API and Electron and test the account. A cookie, a successful process start or HTTP 200 from model discovery does **not** prove usable authentication. Verify a real message as well.
4. Stop the test API and its Electron processes. Remove the link itself with `rmdir C:\Temp\mezhs-test\data\connections\grok-test\profile` before deleting temporary test storage. Do not recursively delete through the junction. Do not expose a reusable account profile to untrusted code.

Live evidence (2026-11-20, base SHA `72eac750eb2e6109d2b50a0f571920c70b1b5301`): an isolated API on port 5354 launched Electron using a directory junction to an inactive Grok profile, and model discovery returned HTTP 200. A real Grok message **failed**: the provider opened `tos-gate` instead of the prompt editor. Junction access is verified; end-to-end provider authorization and message delivery are **not** verified. This result is not an end-to-end PASS.
