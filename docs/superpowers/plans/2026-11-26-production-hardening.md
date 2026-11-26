# Production hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wrap the Data Protection key ring with a certificate and refuse to start in production without one, prove every module migrates over data, exempt `/health/*` from HTTPS redirection, and write the six operator runbooks.

**Architecture:** Auth reads `Auth:DataProtection` certificates in `AddAuthModule` and passes them to the framework's `ProtectKeysWithCertificate` and `UnprotectKeysWithAnyCertificate`; a hosted service checks the certificates and the ring at start, outside Development and Testing. A seed ladder in the integration project walks each context's migrations with seed SQL and verifies the rows at head. The health exemption is a `UseWhen` around `UseHttpsRedirection`. Runbooks and ADR 0042 are docs.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server), xUnit v3 with Shouldly and NSubstitute, Testcontainers, bash scripts.

**Spec:** `docs/superpowers/specs/2026-11-26-production-hardening-design.md` (read it first; section numbers below refer to it).

## Global Constraints

- `TemplateName` is the placeholder; never hard-code a concrete project name. Switch markers (ADR 0035) are comments in the all-on tree, on-only regions, never an `#else`.
- No `DateTime.Now` or `DateTime.UtcNow`; inject `TimeProvider`. No secrets in files; no new package.
- Commit messages are Conventional Commits with a scope from CONTRIBUTING (`feat(host)`, `test(persistence)`, `feat(security)`, `docs(platform)`, `docs(adr)`); `docs(spec)` is rejected. End each with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Tests are written first and are green; the matching `docs/` page is updated in the same task.
- Run `dotnet format --verify-no-changes` before each commit. Warnings are errors in Release.
- Run `bash build/scripts/project-index.sh` and commit the result after any task that changes a configuration key, a test folder or file, or a source file with a paired test; `git add` new files first.
- After a task that changes a marked region or `.template.config/template.json`, run the smoke with each switch off (`--notifications false`, `--includeSample false`, `--caching memory`) as well as the default.
- Integration tests need Docker. If Docker is not usable on the machine, run unit and architecture tests locally and let CI on the PR run the integration tests; say so in the task's report instead of calling it verified.
- The squash title of the PR is `feat(security)!: wrap the key ring with a certificate` with a BREAKING CHANGE footer (production now refuses to start without a certificate). Check that the release PR proposes the next `rc`, not 2.0.0; use release-as if it proposes a major.

## Review Focus

1. A wrong or missing PFX password, or an unreadable file: the message names `Auth:DataProtection:Certificate:Password` or `:Path` and never prints the password or key material (Task 4).
2. A fresh database where `auth.DataProtectionKeys` does not exist yet: the ring check treats it as empty and start-up continues (Task 5).
3. Lookalike paths: `/healthcheck` and `/healthy` are not exempt; `/health/live/` and `/HEALTH/live` are (Task 1).
4. A seed or verify script with a SQL error: the failure names the module, the file and the SQL error, not just "migration failed" (Task 2).
5. A certificate valid only in the future, or one that expired a second ago: refused with the validity dates; a certificate valid now and expiring tomorrow starts (Task 5).

---

## File map

| Area | Create | Modify |
|---|---|---|
| Health | `tests/TemplateName.IntegrationTests/Host/HealthHttpsRedirectionTests.cs` | `src/Host/TemplateName.Api/Program.cs`, `HostCommand.cs` (doc), `HostCommandTests.cs` (comment), `build/docker/Dockerfile`, `build/scripts/container-verify.sh` (comments), `docs/deployment/docker.md`, `iis.md`, `docs/adr/0033-delivery-pipeline.md` |
| Ladder | `tests/TemplateName.IntegrationTests/UpgradePath/{UpgradeLadder,UpgradeLadderRules,UpgradePathTests,UpgradeLadderGuardTests}.cs`, `UpgradePath/Seeds/{Module}/{Migration}.{seed,verify}.sql`, `templates/modulith-module/tests/ProjectPrefix.IntegrationTests/UpgradePath/Seeds/ModuleName/CreateModuleNameSchema.{seed,verify}.sql` | `TemplateName.IntegrationTests.csproj` (embedded resources), `.template.config/template.json`, `build/scripts/tests/module-template-files.test.sh`, `add-module.test.sh` |
| Key ring | `src/Modules/Auth/TemplateName.Modules.Auth/Infrastructure/DataProtection/{KeyRingCertificateOptions,KeyRingCertificates,KeyRingInspector,KeyRingStartupCheck}.cs`, tests under `tests/TemplateName.UnitTests/Auth/DataProtection/`, `tests/TemplateName.IntegrationTests/Auth/KeyRingProtectionTests.cs`, `tests/TemplateName.IntegrationTests/Infrastructure/ProductionHosts.cs` | `AuthModule.cs:133-135`, `HostTests.cs`, `build/scripts/container-verify.sh` |
| Docs | `docs/runbooks/{README,contract-phase,user-locked-out,lost-mfa-device,signing-key-rotation,restore-backup,dead-letter-replay}.md`, `docs/adr/0042-key-ring-protected-by-a-certificate.md` | `docs/README.md`, `docs/deployment/{README,production-checklist,migrations}.md`, `docs/modules/auth.md`, `docs/adr/{0017,0031,0033}-*.md`, `docs/api-compatibility.md`, `docs/coding-conventions.md`, `docs/blueprint-review.md` |

---

### Task 1: Health endpoints skip HTTPS redirection

**Files:**
- Create: `tests/TemplateName.IntegrationTests/Host/HealthHttpsRedirectionTests.cs`
- Modify: `src/Host/TemplateName.Api/Program.cs:122-127`; the "does not follow redirects" remark in `HostCommand.cs:52-53` and the comment at `HostCommandTests.cs:95`; `build/docker/Dockerfile:49-52`; `build/scripts/container-verify.sh:50-51`; `docs/deployment/docker.md:46`; `docs/deployment/iis.md:69,94`; `docs/adr/0033-delivery-pipeline.md` (find the "https port" lines with `grep -rn "https port" docs build`).

**Interfaces:**
- Produces: no new type. Behaviour: with an https port known, a request whose path starts with the segment `/health` (case-insensitive) is answered, not redirected; every other path still redirects with 307.

- [ ] **Step 1: Write the failing tests** in `HealthHttpsRedirectionTests` (`IntegrationTestBase` with `IntegrationTestWebAppFactory`). The client comes from `Factory.WithWebHostBuilder(b => b.UseSetting("HTTPS_PORT", "5443")).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })`.
  - `[Theory] Health_paths_answer_over_plain_http` for `/health/live`, `/health/ready`, `/health/live/`, `/HEALTH/live`: status is not 307 and has no `Location` header (`/health/live` is 200).
  - `[Theory] Other_paths_still_redirect_to_https` for `/api/v1/auth/me`, `/healthcheck`, `/healthy`: status 307 and `Location` starts with `https://localhost:5443/`.
- [ ] **Step 2: Run** `dotnet test tests/TemplateName.IntegrationTests --filter HealthHttpsRedirectionTests`. Expected: the first theory FAILS with 307.
- [ ] **Step 3: Change `Program.cs`** step 6: keep `app.UseHsts()`; replace `app.UseHttpsRedirection()` with `app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase), branch => branch.UseHttpsRedirection());` and extend the step-6 comment (the health endpoints are exempt so a plain local probe works; HSTS sends nothing over HTTP; exposure unchanged because they were anonymous and return only a status).
- [ ] **Step 4: Run the filter again.** Expected: PASS. Then `dotnet test tests/TemplateName.IntegrationTests --filter HostCommandTests` still passes (that test stubs a 307 to prove the probe does not follow redirects; only its comment changes).
- [ ] **Step 5: Docs follow.** `docker.md` loses "Do not set `HTTPS_PORT`..." (say instead that the container still serves HTTP on 8080 and that `/health/*` never redirects); `iis.md` lets `IIS_HEALTH_URL` be `http://localhost/health/ready` and keeps the https URL as an option; the Dockerfile and `container-verify.sh` comments stop carrying the rule; ADR 0033 gets a dated amendment line (2026-11-26) naming this change and the unchanged exposure.
- [ ] **Step 6: Verify and commit.** `dotnet build -c Release`, `dotnet format --verify-no-changes`, `bash build/scripts/dead-reference-check.sh`. Commit `feat(host): exempt the health endpoints from HTTPS redirection`.

---

### Task 2: Upgrade-path ladder (runner and guard rules)

**Files:**
- Create: `tests/TemplateName.IntegrationTests/UpgradePath/{UpgradeLadder,UpgradeLadderRules,UpgradePathTests,UpgradeLadderGuardTests}.cs`; seeds under `UpgradePath/Seeds/Auth/`
- Modify: `tests/TemplateName.IntegrationTests/TemplateName.IntegrationTests.csproj` (add `<EmbeddedResource Include="UpgradePath\Seeds\**\*.sql" />`)

**Interfaces:**
- Produces (all `internal`, in `TemplateName.IntegrationTests.UpgradePath`):
  - `sealed record MigrationRung(string Id, string Name, Type MigrationType)`; `Name` is the id without its timestamp (`20261008053947_InitialAuth` gives `InitialAuth`).
  - `sealed record ModuleLadder(string Module, Type ContextType, IReadOnlyList<MigrationRung> Rungs)`; `Module` is the context name without `DbContext`.
  - `static class UpgradeLadder`: `IReadOnlyList<ModuleLadder> Discover()` (migration types with `[DbContext]` and `[Migration]` in the assemblies `typeof(Program).Assembly` references whose name starts with `TemplateName.`, rungs ordered by id); `IReadOnlyDictionary<string, string> Seeds` and `Verifies` keyed `"{Module}/{Name}"` read from the embedded resources.
  - `static class UpgradeLadderRules`: `IReadOnlyList<string> Violations(IReadOnlyList<ModuleLadder> ladders, ISet<string> seedKeys, ISet<string> verifyKeys)` returning messages for: a seed key with no migration; a seed without a verify or the reverse; a `[ContractMigration]` rung whose previous rung has no seed.

- [ ] **Step 1: Write the failing rule tests** in `UpgradeLadderGuardTests` (no factory, so no container), using synthetic `Migration` subclasses as `MigrationTests` does: `A_seed_for_a_missing_migration_is_reported`, `A_seed_without_a_verify_and_a_verify_without_a_seed_are_reported`, `A_contract_migration_without_a_seed_before_it_is_reported`, `A_ladder_that_follows_the_rules_has_no_violations`; plus `The_real_ladder_has_no_violations` calling `UpgradeLadderRules.Violations(UpgradeLadder.Discover(), UpgradeLadder.Seeds.Keys.ToHashSet(), UpgradeLadder.Verifies.Keys.ToHashSet())` and `Discover_finds_the_auth_migrations_in_order` (asserts `Auth` has `InitialAuth` first and `AddPhoneCodePurpose` last).
- [ ] **Step 2: Run** `dotnet test tests/TemplateName.IntegrationTests --filter UpgradeLadderGuardTests`. Expected: compile errors, then FAIL.
- [ ] **Step 3: Implement** `UpgradeLadder` and `UpgradeLadderRules` to the interfaces above. Run the filter. Expected: PASS.
- [ ] **Step 4: Write the failing runner test** `UpgradePathTests.Every_context_migrates_over_seeded_data` (factory class). Approach: `Factory.CreateEmptyDatabaseAsync("TemplateName_UpgradePath", Ct)` gives a connection string; a host from `Factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Database", cs))` gives each registered `ModuleDbContextRegistration` context (as `MigrateCommandTests` does). For each ladder: `context.GetService<IMigrator>()`, `MigrateAsync(rung.Id)` per rung in order, then `context.Database.ExecuteSqlRawAsync(seed)` when `Seeds` has `"{Module}/{Name}"`; after the last rung, run each `Verifies` script for that module over a `SqlConnection`; any returned row is a violation. Collect failures as `"{Module}/{Name}: {message}"` (a SQL error from a seed names its file) and assert none with `ShouldBeEmpty(string.Join(Environment.NewLine, failures))`. Seed and verify scripts are plain T-SQL, no `GO`.
- [ ] **Step 5: Add the Auth seeds** (columns come from each migration's `*.Designer.cs` model snapshot and `LegacyUserRows`; fixed ids and dates so the verify scripts name exact rows):
  - `AddRoleAndAuditLogListIndexes`: a user and an open session; verify: the session's `AuthenticatedAt` equals its `CreatedAt` (the backfill of `AddMultiFactorAuthentication`).
  - `AddCleanupIndexes`: a user and an MFA challenge; verify: its `FirstFactor` is `pwd` (the backfill of `AddExternalLogins`).
  - `AddPhoneCodeRequestedAt`: a user and a phone verification code; verify: its `Purpose` is 1 (`Verify`, from `AddPhoneCodePurpose`) and the code row still exists.
  - `InitialAuth`: a role, a user and the user's role; verify: all three still exist.
  Each verify selects the wrong state and returns no rows when right (for example `SELECT 'session lost' WHERE NOT EXISTS (...)`).
- [ ] **Step 6: Run** `dotnet test tests/TemplateName.IntegrationTests --filter UpgradePathTests`. Expected: PASS. Then prove it detects loss: temporarily add `migrationBuilder.Sql("DELETE FROM [auth].[UserSessions]")` at the end of `Up` in `AddMultiFactorAuthentication`, run again (Expected: FAIL naming `Auth/AddRoleAndAuditLogListIndexes`), and `git checkout` the migration. Also prove a broken seed names its file: temporarily misspell a column in a seed (Expected: the failure names that file), then restore.
- [ ] **Step 7: Verify and commit.** `dotnet format --verify-no-changes`; `bash build/scripts/project-index.sh`; commit `test(persistence): add the upgrade-path ladder`.

---

### Task 3: Seed every other context and wire the template

**Files:**
- Create: seeds under `UpgradePath/Seeds/{Platform,Audit,Notifications,Sample}/`; `templates/modulith-module/tests/ProjectPrefix.IntegrationTests/UpgradePath/Seeds/ModuleName/CreateModuleNameSchema.{seed,verify}.sql`
- Modify: `UpgradeLadderRules.cs` and its tests; `.template.config/template.json` (the `exclude` lists at ~128-132 and ~141-143); `build/scripts/tests/module-template-files.test.sh`, `build/scripts/tests/add-module.test.sh` (expected files); `docs/coding-conventions.md` (authoring rule, spec 4.3)

**Interfaces:**
- Consumes: Task 2's `UpgradeLadder`, `UpgradeLadderRules`.
- Produces: `UpgradeLadderRules.Violations` also reports a context that has migrations and no seed.

- [ ] **Step 1: Write the failing rule test** `A_context_without_any_seed_is_reported` in `UpgradeLadderGuardTests`. Run it: FAIL. Implement the rule. Expected: the real-ladder test now FAILS naming `Platform`, `Audit`, `Notifications` and `Sample`.
- [ ] **Step 2: Add seeds** that cover the first migration of each context and any later one that rewrites or backfills data: `Platform/InitialPlatform` (an idempotency record and a job state), `Audit/CreateAuditSchema` (an audit log row), `Notifications/InitialNotifications` (a delivery) and `Notifications/AddDeliveryProviderMessageId` (a delivery that must keep its status), `Sample/InitialSample` (a leave request). Each verify returns no rows when the seeded rows survive with their key values.
- [ ] **Step 3: Run** the guard and runner filters. Expected: PASS.
- [ ] **Step 4: Template wiring.** Add the Notifications and Sample seed folders to the matching `exclude` lists in `.template.config/template.json`. Add the module template's placeholder seed (a comment line saying there is no table yet and where to add rows) and verify (`SELECT 1 WHERE 1 = 0`) and register both in the two `build/scripts/tests/*.test.sh` expected-file lists. Add the authoring rule to `docs/coding-conventions.md`.
- [ ] **Step 5: Verify.** `bash build/scripts/tests/module-template-files.test.sh`; `bash build/scripts/template-smoke.sh`, then with `--notifications false`, `--includeSample false`, `--caching memory`, then `--add-module Reports` (about 5 minutes); in the generated `Reports` project, `dotnet test tests/*.IntegrationTests --filter UpgradeLadderGuardTests` passes.
- [ ] **Step 6: Commit** `test(persistence): seed every module in the upgrade-path ladder` after `bash build/scripts/project-index.sh`.

---

### Task 4: Wrap the key ring with a certificate

**Files:**
- Create: `Infrastructure/DataProtection/KeyRingCertificateOptions.cs`, `KeyRingCertificates.cs` under `src/Modules/Auth/TemplateName.Modules.Auth/`; `tests/TemplateName.UnitTests/Auth/DataProtection/{KeyRingCertificatesTests,KeyRingProtectionTests}.cs`, `TestCertificates.cs`
- Modify: `AuthModule.cs:133-135`

**Interfaces:**
- Produces (`internal`, namespace `TemplateName.Modules.Auth.Infrastructure.DataProtection`):
  - `enum CertificateSource { Store, File }`; `sealed class CertificateSettings { CertificateSource Source; string? Thumbprint; StoreLocation StoreLocation = LocalMachine; StoreName StoreName = My; string? Path; string? Password; }`; `sealed class KeyRingCertificateOptions { const string SectionName = "Auth:DataProtection"; CertificateSettings? Certificate; List<CertificateSettings> PreviousCertificates = []; }`.
  - `sealed record KeyRingCertificateSet(X509Certificate2? Active, IReadOnlyList<X509Certificate2> Previous, IReadOnlyList<string> Problems)` with `bool Configured`.
  - `static class KeyRingCertificates`: `KeyRingCertificateSet Load(KeyRingCertificateOptions options)` and `void Protect(IDataProtectionBuilder builder, KeyRingCertificateSet set)`. `Load` takes a certificate from the store by thumbprint or from a PFX with `X509CertificateLoader`. A problem string names the setting (`Auth:DataProtection:Certificate:Path`, `...:PreviousCertificates:0:Thumbprint`) for: missing source or fields, file not found, wrong password, not found in the store, no private key, not RSA, previous certificates with no active one. It never includes the password or key material. It does not check validity dates (Task 5 does, with `TimeProvider`).
  - `TestCertificates` (unit tests): `Create(string name, DateTimeOffset notBefore, DateTimeOffset notAfter, bool withPrivateKey = true)` returning an `X509Certificate2`, and `WritePfx(X509Certificate2, string directory, string password)` returning the path; certificates are generated in memory, none is committed.

- [ ] **Step 1: Write the failing tests.** `KeyRingCertificatesTests`: `Loads_a_file_certificate`, `Loads_previous_certificates`, `A_missing_file_is_a_problem_naming_the_path_setting`, `A_wrong_password_is_a_problem_that_does_not_contain_the_password`, `A_certificate_without_a_private_key_is_a_problem`, `A_non_RSA_certificate_is_a_problem`, `Previous_certificates_without_an_active_one_are_a_problem`, `No_settings_is_not_configured_and_has_no_problem`. `KeyRingProtectionTests` builds a `ServiceCollection` with `AddDataProtection().SetApplicationName(...).PersistKeysToFileSystem(tempDir)` plus `KeyRingCertificates.Protect`: `A_second_provider_reads_a_protected_ring`, `The_key_file_holds_an_encryptedSecret_and_no_masterKey`, `After_rotation_old_keys_stay_readable` (active B, previous A; create a new key with `IKeyManager.CreateNewKey`; both payloads read), `Without_the_old_certificate_old_keys_are_not_readable`.
- [ ] **Step 2: Run** `dotnet test tests/TemplateName.UnitTests --filter DataProtection`. Expected: compile errors, then FAIL.
- [ ] **Step 3: Implement** the types above. `Protect` does nothing when `Active` is null, else `ProtectKeysWithCertificate(Active)` then `UnprotectKeysWithAnyCertificate([Active, .. Previous])`. A spike on 2026-11-26 proved this works and that the framework's thumbprint overload is eager (so it is not used).
- [ ] **Step 4: Register in `AuthModule`**: bind `KeyRingCertificateOptions` from `configuration.GetSection("Auth:DataProtection")` (the existing `ApplicationName` key stays read as it is), `KeyRingCertificates.Load`, `AddSingleton(set)`, and `KeyRingCertificates.Protect(builder, set)` after `PersistKeysToDbContext<AuthDbContext>()`. Registration never throws for a certificate problem.
- [ ] **Step 5: Run** the filter, then `dotnet test tests/TemplateName.UnitTests tests/TemplateName.ArchitectureTests`. Expected: PASS.
- [ ] **Step 6: Commit** `feat(security): wrap the Data Protection ring with a certificate` after `dotnet format --verify-no-changes`.

---

### Task 5: Refuse to start without a usable key ring

**Files:**
- Create: `Infrastructure/DataProtection/{KeyRingInspector,KeyRingStartupCheck}.cs`; `tests/TemplateName.UnitTests/Auth/DataProtection/{KeyRingInspectorTests,KeyRingStartupCheckTests}.cs`; `tests/TemplateName.IntegrationTests/Auth/KeyRingProtectionTests.cs`; `tests/TemplateName.IntegrationTests/Infrastructure/ProductionHosts.cs`
- Modify: `AuthModule.cs` (register the hosted service), `tests/TemplateName.IntegrationTests/Host/HostTests.cs` (the two Production tests), `build/scripts/container-verify.sh`

**Interfaces:**
- Consumes: Task 4's `KeyRingCertificateSet`.
- Produces:
  - `readonly record struct KeyRingInspection(int PlaintextKeys, IReadOnlySet<string> WrappingThumbprints)`; `static class KeyRingInspector { KeyRingInspection Inspect(IEnumerable<string> documents); }`. A `<key>` document with no `encryptedSecret` is plaintext; with one, its embedded `X509Certificate` gives the SHA-1 thumbprint (upper-case hex); documents whose root is not `key` are ignored. Pure; no database.
  - `internal interface IKeyRingReader { Task<IReadOnlyList<string>> ReadDocumentsAsync(CancellationToken cancellationToken); }` and `internal sealed class DbKeyRingReader(IServiceScopeFactory scopes) : IKeyRingReader`, which reads `AuthDbContext.DataProtectionKeys` (the `Xml` column) through a scope; a missing table (SQL error 208) returns an empty list. Registered as a singleton.
  - `internal sealed class KeyRingStartupCheck : IHostedService`, constructor `(KeyRingCertificateSet certificates, IKeyRingReader ring, IHostEnvironment environment, TimeProvider time)`. `StartAsync` returns at once when `SigningKeyProvider.AllowsEphemeralKey(environment)`; otherwise it throws `InvalidOperationException` listing every problem. Problems: those of the set; no active certificate (`Auth:DataProtection:Certificate` not configured); the active certificate outside `NotBefore..NotAfter` at `time.GetUtcNow()` (message gives both dates); a plaintext key; a key wrapped under a thumbprint that is neither the active nor a previous certificate (message names the thumbprint). Each message names the setting and `docs/runbooks/restore-backup.md`.

- [ ] **Step 1: Write the failing inspector tests**, feeding `KeyRingInspector` XML that the framework produced in the test (a temp-directory ring made with and without `KeyRingCertificates.Protect`): `A_ring_made_without_a_protector_has_plaintext_keys`, `A_ring_made_with_a_certificate_has_none_and_names_its_thumbprint`, `A_revocation_document_is_ignored`, `Several_keys_under_two_certificates_name_both_thumbprints`.
- [ ] **Step 2: Write the failing check tests** (`KeyRingStartupCheckTests`, a `FakeTimeProvider`, a substitute `IKeyRingReader`): `Development_and_Testing_never_fail`, `Production_with_no_certificate_fails_naming_the_setting`, `A_certificate_valid_only_in_the_future_fails_with_the_dates`, `A_certificate_that_expired_a_second_ago_fails`, `A_certificate_valid_now_and_expiring_tomorrow_starts`, `A_plaintext_key_fails`, `A_key_under_an_unconfigured_certificate_fails_naming_the_thumbprint`, `The_message_never_contains_the_password`.
- [ ] **Step 3: Run** `dotnet test tests/TemplateName.UnitTests --filter KeyRing`. Expected: compile errors, then FAIL. Implement both types; register `services.AddHostedService<KeyRingStartupCheck>()` in `AuthModule`. Run again: PASS.
- [ ] **Step 4: Production host helper.** `ProductionHosts.CreateAsync(IntegrationTestWebAppFactory factory, string databaseName, CancellationToken ct)` returns a `WebApplicationFactory<Program>`: an empty database from `CreateEmptyDatabaseAsync`, migrated with `HostCommand.RunMigrationsAsync` on a Testing host as `MigrateCommandTests` does, then `WithWebHostBuilder` with `UseEnvironment("Production")`, a fresh signing key, and a throwaway PFX (generated in a temp directory, password random) set through `Auth:DataProtection:Certificate:*`. Switch `HostTests.Scalar_is_not_mapped_in_production` to it.
- [ ] **Step 5: Write the failing integration tests** (`KeyRingProtectionTests`, factory): `Production_starts_with_a_certificate_and_writes_a_protected_ring` (after a request that protects something, every `auth.DataProtectionKeys` row has an `encryptedSecret`), `Production_does_not_start_without_a_certificate` (message contains `Auth:DataProtection:Certificate`), `Production_refuses_a_plaintext_ring` (insert one plaintext key row, using XML from the inspector fixture), `Migrate_runs_in_production_with_no_certificate` (`HostCommand.RunMigrationsAsync` on a Production host built without certificate settings returns 0), `The_ring_reader_treats_a_missing_table_as_an_empty_ring` (`DbKeyRingReader` over an empty, unmigrated database returns an empty list). Keep `HostTests.Production_does_not_start_without_a_signing_key` passing. Run: FAIL where the behaviour is missing, then PASS after Steps 3-4 (CI if no Docker).
- [ ] **Step 6: `container-verify.sh`.** Generate a throwaway PFX with `openssl` (key, self-signed certificate, `openssl pkcs12 -export` with a random password held in an environment variable) in `$work`, mode 644, mount it read-only into the API container, and pass `Auth__DataProtection__Certificate__Source=File`, `...__Path=/certs/dp.pfx` and `...__Password` (`-e NAME` form, as the signing key is). Add one step: run the image in Production without the certificate and expect a non-zero exit and `Auth:DataProtection:Certificate` in the log. Update the header comment listing the minimum Production configuration. Run `bash -n build/scripts/container-verify.sh`; the full run is the `container` CI job.
- [ ] **Step 7: Verify and commit.** `dotnet test tests/TemplateName.UnitTests tests/TemplateName.ArchitectureTests`; default smoke and the three switch-off smokes; `bash build/scripts/project-index.sh`; commit `feat(security): refuse to start in production without a usable key ring`.

---

### Task 6: Runbooks

**Files:**
- Create: `docs/runbooks/{README,contract-phase,user-locked-out,lost-mfa-device,signing-key-rotation,restore-backup,dead-letter-replay}.md`
- Modify: `docs/README.md`, `docs/deployment/README.md`, `docs/deployment/production-checklist.md` (links only; the checklist text changes in Task 7)

**Interfaces:** the runbook paths above are what Tasks 4, 5 and 7 link to; `restore-backup.md` is named in the startup-check messages.

Each page follows one shape: **When**, **Who** (role or permission), **Steps** (numbered, each with the exact command, route or SQL), **Verify**, **Roll back**. Content, from spec section 5:

- [ ] **Step 1: `contract-phase.md`**: the order from `docs/api-compatibility.md` (expand, release, confirm nothing reads the old column, contract with `[ContractMigration]` under ADR 0034's maintenance mode), the query or log check for "nothing reads it", and the upgrade-ladder seed required before the contract migration (Task 3 rule).
- [ ] **Step 2: `user-locked-out.md` and `lost-mfa-device.md`**: each a short page linking to "Recovering access" in `docs/modules/auth.md` and naming the routes (`POST /api/v1/admin/auth/users/{id}/unlock`, `/reset-mfa`), the permission each needs, and the identity check before using them.
- [ ] **Step 3: `signing-key-rotation.md`**: add the new key as current in `Auth:Jwt:SigningKeys`, deploy, keep the old key (public half) listed for the access-token lifetime, retire it; verify with the key-set route if `docs/modules/auth.md` documents one, otherwise omit.
- [ ] **Step 4: `restore-backup.md`**: restore the database and the key-ring certificate (and every previous certificate) together; verify start-up passes the ring check; how to list the certificates a ring needs (the thumbprints the startup message prints); "start a new protected ring" steps for a plaintext ring or a lost certificate (clear `auth.DataProtectionKeys` with the API stopped; consequences: TOTP secrets, queued outbox secrets and links protected by the old ring stop working, so users re-enrol MFA); rotation of the active certificate (add new as active, move old to `PreviousCertificates`, keep it for as long as the ring exists), and "rotate before the expiry date".
- [ ] **Step 5: `dead-letter-replay.md`**: the three-step SQL from spec 5.1 for one module schema (the abandoned `SELECT` with `ProcessedAt IS NULL AND AttemptCount >= 5`, the one-transaction `UPDATE` resetting `AttemptCount`, `NextAttemptAt`, `LockedUntil`, `Error` for chosen ids with an `@@ROWCOUNT` check, the gauge `platform.outbox.abandoned` to watch), why replay is safe (`InboxMessages`), and the sanctioned-exception sentence. The line naming Notifications' `Retry` endpoint sits on its own `<!--#if (notifications)-->` marked line.
- [ ] **Step 6: Index and links.** `README.md` lists the six pages with one line each; `docs/README.md` and `docs/deployment/README.md` link it. Verify: `bash build/scripts/dead-reference-check.sh`, `dotnet test tests/TemplateName.ArchitectureTests` (document tests), `bash build/scripts/template-smoke.sh --notifications false` (the marked line). Commit `docs(platform): add the six runbooks`.

---

### Task 7: ADR, checklist and the remaining docs

**Files:**
- Create: `docs/adr/0042-key-ring-protected-by-a-certificate.md`
- Modify: `docs/adr/0017-outbox-secrets-protected-with-data-protection.md` (follow-up note), `0031-api-and-database-compatibility-policy.md`, `0033-delivery-pipeline.md` ("Not built" list), `docs/deployment/production-checklist.md`, `docs/deployment/{docker,iis,migrations}.md`, `docs/modules/auth.md` (config table near `Auth:DataProtection:ApplicationName`, known limits, "Recovering access"), `docs/api-compatibility.md`, `docs/blueprint-review.md` (row 135), `docs/architecture/overview.md` if it lists the ring as unprotected

- [ ] **Step 1: ADR 0042** (same shape as ADR 0041): context (ring unencrypted at rest, ADR 0017); decision (certificate only, `Auth:DataProtection:Certificate` and `PreviousCertificates`, startup refusal outside Development and Testing, plaintext and unknown-certificate rings refused, no bypass flag); consequences (the framework wraps with RSA-1.5 key transport; certificates that wrapped a key are kept for as long as the ring exists; Key Vault and DPAPI are project-side swaps; no live conversion); alternatives (a building-block package, a single thumbprint, a bypass flag).
- [ ] **Step 2: Amend ADRs.** ADR 0017's follow-up line points to 0042; ADR 0031's "Not built" upgrade-path entry points to `UpgradePath/` and states the ladder now ships; ADR 0033's "Not built" list drops the upgrade-path test, key-ring protector, runbooks and HTTPS items that are now built.
- [ ] **Step 3: Checklist and module docs.** In `production-checklist.md` the row "Persisted; **not protected**" becomes satisfied, and the section "Data Protection keys persisted and protected" documents the settings, the refusal and the runbooks; `docker.md` and `iis.md` document mounting the PFX or the store grant (the app pool identity needs read access to the private key) and the certificate password as a secret; `migrations.md` and `api-compatibility.md` describe the ladder in place of "a project is required to have it". `auth.md` lists `Auth:DataProtection:Certificate:*` and `PreviousCertificates` in its configuration table and updates the "not protected" mentions at ~1401 and ~1585.
- [ ] **Step 4: Roadmap.** In `docs/blueprint-review.md` row 135 mark the production-hardening follow-ups built (this change), leaving the maintenance-screen detection as the remaining gate.
- [ ] **Step 5: Verify and commit.** `bash build/scripts/project-index.sh`; `bash build/scripts/dead-reference-check.sh`; `dotnet test tests/TemplateName.ArchitectureTests`; the default smoke and the three switch-off smokes; `bash build/scripts/template-smoke.sh --from-package --generate-only` only if `build/pack/` or the package excludes changed (they should not have). Commit `docs(adr): record the production hardening decisions`.

---

## Self-review (against the spec)

- Spec 3 (key protection): Tasks 4 and 5. Spec 4 (ladder): Tasks 2 and 3. Spec 5 (runbooks): Task 6. Spec 6 (health): Task 1. Spec 7 (docs, ADR, `container-verify.sh`, template): Tasks 1, 3, 5, 7. Spec 9 risks: the refusal and the certificate-access message are in Task 5 and the IIS grant in Task 7.
- Deviations from the spec's original wording, all made after the 2026-11-26 spike and written into the spec: the certificate is read in `AddAuthModule` (not lazily), the plaintext marker is `encryptedSecret`, a ring wrapped under an unconfigured certificate is refused, and the guard tests live in the integration project (they need no Docker).
- Types used across tasks (`KeyRingCertificateSet`, `KeyRingCertificates.Load` and `.Protect`, `KeyRingInspection`, `UpgradeLadder`, `UpgradeLadderRules.Violations`, `ProductionHosts.CreateAsync`) are defined where they are produced.
