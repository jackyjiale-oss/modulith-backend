@build/template-content/CLAUDE.md

## Maintaining the template

- `TemplateName` is the placeholder; never hard-code a concrete project name.
- Files under `build/template-content/` replace their root counterparts in generated output.
- Run `bash build/scripts/template-smoke.sh` after changing anything that ships.
- `BACKEND_TEMPLATE_BLUEPRINT.md` and `docs/blueprint-review.md` are the spec; the review wins.
