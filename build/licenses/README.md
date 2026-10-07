# Licence check

The `licenses` CI job runs [`nuget-license`](https://github.com/sensslen/nuget-license) (Apache-2.0, pinned in `.config/dotnet-tools.json`) over every project in the solution, transitive packages included. It fails when a package's licence is not in `allowed-licenses.json`. Run it locally after a restore:

```bash
dotnet tool restore
dotnet nuget-license -i TemplateName.slnx -t \
  -a build/licenses/allowed-licenses.json \
  -mapping build/licenses/license-url-mappings.json \
  -override build/licenses/package-overrides.json \
  -ignore build/licenses/ignored-packages.json \
  -err
```

## Files

| File | Holds | Rule for adding an entry |
|---|---|---|
| `allowed-licenses.json` | The allowed SPDX identifiers: MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause. | Only with an ADR (CONTRIBUTING, Section 6). |
| `license-url-mappings.json` | Licence URLs of packages that declare only a URL, mapped to the SPDX identifier the URL's text was verified to be. | Read the licence text at the URL; add the entry with its justification below. |
| `package-overrides.json` | Packages whose metadata has no licence at all, with the licence verified in their source repository. Pinned to one version, so an upgrade fails until it is verified again. | Read the licence in the package's source repository at that version; add the entry with its justification below. |
| `ignored-packages.json` | Packages that are not under an allowed licence but that the stack cannot avoid. | Only with an ADR (CONTRIBUTING, Section 6), listed below. Entries are exact package names; the tool cannot pin versions here. |

JSON cannot hold comments, so the justification for every entry is here.

## Verified mappings and overrides

| Entry | Licence | Evidence |
|---|---|---|
| URL `http://opensource.org/licenses/mit-license.php` (used by `Mono.Cecil` 0.11.3, a dependency of `NetArchTest.Rules`) | MIT | The URL is the Open Source Initiative's MIT licence page; the package's repository, `jbevain/cecil`, ships the MIT licence in `LICENSE.txt`. |
| `NetArchTest.Rules` 1.3.2 (architecture tests only) | MIT | The package metadata has no licence; the repository `BenMorris/NetArchTest` carries the MIT licence ("MIT License, Copyright (c) 2018 Ben Morris") in `LICENSE`. |

## Ignored packages: Microsoft redistributable runtime components (ADR 0012)

| Package | Comes from | Licence |
|---|---|---|
| `Microsoft.Data.SqlClient.SNI.runtime` | `Microsoft.EntityFrameworkCore.SqlServer` → `Microsoft.Data.SqlClient` | "MICROSOFT SOFTWARE LICENSE TERMS – MICROSOFT.DATA.SQLCLIENT.SNI LIBRARY" (`LICENSE.txt` in the package): proprietary, Distributable Code that may be shipped inside applications. |
| `Microsoft.Identity.Client.NativeInterop` | `Microsoft.Data.SqlClient` → `Microsoft.Identity.Client.Broker` | "MICROSOFT SOFTWARE LICENSE TERMS" (`LICENSE` in the package): proprietary. |

Both are native runtime pieces of Microsoft's SQL Server client, which SQL Server only (ADR 0003) makes unavoidable. [ADR 0012](../../docs/adr/0012-sqlclient-native-runtime-licence-exemption.md) exempts exactly these two names: the allow-list is not widened, and every other package is still checked. When `Microsoft.Data.SqlClient` or `Microsoft.EntityFrameworkCore.SqlServer` is upgraded, re-read both licence files at the new versions; changed terms need a new ADR.
