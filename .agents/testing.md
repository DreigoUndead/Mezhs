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
- Create a git-ignored local shortcut in the task worktree (for example under `profiles/`) to the owner-provided credential source. A shortcut does not grant permission to open an active browser profile. Never log, commit, or attach credentials or cookies. Only the explicitly owner-authorized one-use snapshot procedure below permits cloning an active profile.
- Sign in through the separate test browser. Request owner interaction only for actions that cannot be performed safely by the agent, such as entering credentials or completing MFA. Verify real provider message delivery after signing in.
- An active owner Chromium profile is not a reusable test profile, even if its path is known. Do not point the isolated test instance at it.

## Reusing an inactive browser profile with a Windows junction

An isolated Windows MEZS test may link to a dedicated persistent, **inactive** test-account Chromium profile rather than copy cookies or sign in on every worktree. This is a shared directory, **not** an isolated copy: the test browser can modify the target profile. Never open the same profile in two Electron/Chromium processes. An idle owner application may still have its profile open.

1. Keep the test API port, configuration, storage root, databases and other connections separate. Create the test connection parent directory, but leave its `profile` destination absent.
2. On Windows, create a directory junction using `mklink /J C:\Temp\mezhs-test\data\connections\grok-test\profile D:\Mezhs-TestProfiles\grok-account\profile`. Quote paths if they contain spaces. Use an inactive **test** profile, never an active owner browser profile.
3. Verify that the destination is a junction pointing to the intended source. Start the isolated API and Electron and test the account. A cookie, a successful process start or HTTP 200 from model discovery does **not** prove usable authentication. Verify a real message as well.
4. Stop the test API and its Electron processes. Remove the link itself with `rmdir C:\Temp\mezhs-test\data\connections\grok-test\profile` before deleting temporary test storage. Do not recursively delete through the junction. Do not expose a reusable account profile to untrusted code.

Live evidence (2026-11-20, base SHA `72eac750eb2e6109d2b50a0f571920c70b1b5301`): an isolated API on port 5354 launched Electron using a directory junction to an inactive Grok profile, and model discovery returned HTTP 200. A real Grok message **failed**: the provider opened `tos-gate` instead of the prompt editor. Junction access is verified; end-to-end provider authorization and message delivery are **not** verified. This result is not an end-to-end PASS.

## Owner-authorized VSS snapshot clone for isolated live tests

**Exception, not the default.** Prefer a dedicated inactive authenticated test profile or separate interactive sign-in. Only with explicit owner authorization, a one-use clone of an active Chromium profile may be assembled from a Windows Volume Shadow Copy (VSS) without shutting down the owner application. Never point a test browser or junction at the active source profile. The clone contains sensitive account credentials: never print, commit, attach or preserve them beyond testing.

1. Identify the exact owner source profile and owning API/Electron process tree. Record authorization to use a disposable clone. Prepare a separate task-revision worktree, test configuration, API port, storage root, connection ID and Electron processes; never modify owner settings or storage.
2. Copy ordinary nonlocked profile files to a separate temporary directory. To recover locked Chromium Network/Cookies and companion journal/WAL files, create a short-lived VSS snapshot of the source volume (for example, elevated PowerShell using Win32_ShadowCopy.Create with ClientAccessible). Read snapshot files through the GLOBALROOT shadow-device path using System.IO.File.Copy; robocopy may not accept that path. Prefer files from the same consistent snapshot where possible. Always delete the VSS snapshot in a finally path. Never unlock files by killing owner processes.
3. Verify the clone path differs from the live source, inspect copied-file completeness and run SQLite PRAGMA integrity_check on copied cookie databases when available. A copied cookie database or HTTP 200 from model discovery alone does not prove a usable signed-in session. Stop if the clone is inconsistent.
4. Create a Windows directory junction only from the isolated test connection profile path to the temporary **clone**, verify LinkType is Junction and Target matches the clone exactly, then start the isolated API and browser. Verify real provider message delivery, continuation and relevant task-specific scenarios. Do not resend a mutation with an uncertain submission outcome. Distinguish observed behavior from hypotheses.
5. Stop only the verified isolated API and its Electron process tree; check the owner API remains running. Remove the junction itself with cmd /c rmdir before recursively deleting any temporary directory. Then remove the clone, isolated test storage and temporary helpers; verify there is no leftover shadow copy or test process. Never recursively delete through a junction.

**Observed 2026-11-23:** Under explicit owner direction not to interrupt active MEZS agents, an isolated task-branch API on port 5374 used a disposable VSS-assisted clone and authenticated successfully. Live new/continued turns, overlapping requests, project resolution and a later file-reading turn completed; an earlier attachment stream failure remained unexplained and signed output-file download was unverified. This demonstrates a workable isolated verification method, **not** overall Testing PASS or a universal guarantee that cloned credentials will work.
