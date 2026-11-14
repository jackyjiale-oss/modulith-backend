@build/template-content/CLAUDE.md

## Maintaining the template

- `TemplateName` is the placeholder; never hard-code a concrete project name.
- Files under `build/template-content/` replace their root counterparts in generated output.
- Run `bash build/scripts/template-smoke.sh` after changing anything that ships.
- Switch markers (`//#if (notifications)`, `<!--#if (includeSample)-->` and the like, ADR 0035) are comments in the all-on tree: only on-only regions, never an `#else`. A sentence or line naming a removable part sits on its own marked line.
- After changing a marked region, run the smoke with each switch off as well: `bash build/scripts/template-smoke.sh --notifications false`, then `--includeSample false`, then `--caching memory`.
- After changing a route, permission, error code, integration event or its handler, recurring job, configuration key or its documentation, test folder or switch marker, after adding, removing or renaming a test file or a source file that has a paired test, or whenever the `project-index` check fails, run `bash build/scripts/project-index.sh` and commit the result; stage new files first (`git add`): the generator reads tracked files only. Start a lookup at `docs/superpowers/index/MAP.md`.
- `BACKEND_TEMPLATE_BLUEPRINT.md` and `docs/blueprint-review.md` are the spec; the review wins.
