@build/template-content/CLAUDE.md

## Maintaining the template

- `TemplateName` is the placeholder; never hard-code a concrete project name.
- Files under `build/template-content/` replace their root counterparts in generated output.
- Run `bash build/scripts/template-smoke.sh` after changing anything that ships.
- Switch markers (`//#if (notifications)`, `<!--#if (includeSample)-->` and the like, ADR 0035) are comments in the all-on tree: only on-only regions, never an `#else`. A sentence or line naming a removable part sits on its own marked line.
- After changing a marked region, run the smoke with each switch off as well: `bash build/scripts/template-smoke.sh --notifications false`, then `--includeSample false`, then `--caching memory`.
- `BACKEND_TEMPLATE_BLUEPRINT.md` and `docs/blueprint-review.md` are the spec; the review wins.
