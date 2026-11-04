# Verification — standardize-development-workflow

**Branch:** `standardize-development-workflow`
**Status:** review candidate; final checks and owner approval pending.

## Checks to run

- Compare all documentation changes against `main`; no application runtime source changes expected.
- Check internal relative links and references to removed files.
- Verify the three task levels, conditional independent agent usage and **Review → Test** order are consistent across guides.
- Verify Small changes can use one chat, with mandatory **pause for owner approval**.
- Verify plan movement, handoff metadata, evidence SHA and revalidation after base changes.
- Check planning and implementation PR sections separately.
- Check that `VERSION` and `CHANGELOG.md` are not pre-generated on the development branch, and that release instructions update them once on `main` at merge time.

## Evidence

Record exact inspected SHA, checks/CI results and limitations in the PR or here. Do not claim independent validation without it.

**Owner approval:** pending. Do not merge automatically.
