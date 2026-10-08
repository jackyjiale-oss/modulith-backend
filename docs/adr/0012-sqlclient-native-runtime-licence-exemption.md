# 0012. Microsoft SqlClient native runtime components exempt from the licence allow-list

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
Every NuGet dependency, transitive ones included, must be under MIT, Apache-2.0, BSD-2-Clause or BSD-3-Clause unless an ADR allows otherwise. The `licenses` CI job enforces this with `nuget-license` against `build/licenses/allowed-licenses.json`. Two transitive packages fail the check:

| Package | Pulled in by | Licence file in the package |
|---|---|---|
| `Microsoft.Data.SqlClient.SNI.runtime` (6.0.2 today) | `Microsoft.EntityFrameworkCore.SqlServer` → `Microsoft.Data.SqlClient` | `LICENSE.txt`, headed "MICROSOFT SOFTWARE LICENSE TERMS / MICROSOFT.DATA.SQLCLIENT.SNI LIBRARY": proprietary terms that allow the object code to be distributed as part of an application ("Distributable Code") |
| `Microsoft.Identity.Client.NativeInterop` (0.20.6 today) | `Microsoft.Data.SqlClient` → `Microsoft.Identity.Client.Broker` | `LICENSE`, headed "MICROSOFT SOFTWARE LICENSE TERMS": proprietary terms |

Both are native runtime pieces of Microsoft's SQL Server client. SQL Server is the only supported database (ADR 0003), and `Microsoft.Data.SqlClient` is the only maintained SQL Server client for .NET and EF Core, so neither package can be avoided.

## Options considered
1. Add the Microsoft licence to `allowed-licenses.json`: the check passes, but every future package under any Microsoft proprietary terms would pass too.
2. Leave the check failing, or check direct dependencies only: no gate at all, or a gate that never sees transitive packages.
3. Exempt exactly these two packages by name and keep the allow-list unchanged.

## Decision
We chose option 3.

- `build/licenses/ignored-packages.json` lists `Microsoft.Data.SqlClient.SNI.runtime` and `Microsoft.Identity.Client.NativeInterop` by their exact names, with no wildcards. The allow-list is **not** widened, and every other package, including the dependencies of these two, is still checked.
- `nuget-license` ignores packages by name only (its ignore list accepts names and wildcards, not versions), so the exemption cannot be pinned to the versions above, unlike the entries in `package-overrides.json`.
- **Review trigger:** whenever `Microsoft.Data.SqlClient` or `Microsoft.EntityFrameworkCore.SqlServer` is upgraded (a Dependabot PR included), the reviewer re-reads the licence files of both packages at the new versions in the lock file. A change of terms, or a new package under non-allowed terms, needs a new ADR.
- The release workflow lists both packages in `THIRD-PARTY-NOTICES.md` (`-include-ignored`), so they are still declared to users.

## Consequences
- Positive: the stack's SQL Server client works and the licence gate stays strict for everything else; the exemption is visible in one file with its reason here.
- Negative / trade-offs accepted: two proprietary Microsoft runtime components ship in every build; their terms (distribution only as part of an application, no reverse engineering) apply to projects generated from the template; the name-only exemption relies on review when SqlClient is upgraded.
- Follow-up actions: drop the entries if Microsoft republishes the packages under an allowed licence, or if a future SqlClient no longer depends on them.
