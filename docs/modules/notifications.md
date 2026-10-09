# Notifications module

<!-- Copied from docs/modules/_template.md. Each Notifications task updates the sections it changes; the module is built up over Plan 3. -->

## Purpose and boundaries

<!-- What the module owns, what it deliberately does not do, and which other modules it talks to (only through *.Contracts). -->

Every message the template sends goes through this module: it consumes the integration events other modules publish, renders a localized message in the **recipient's** language and delivers it by email and in-app. Plan 3 builds it task by task; this page describes what exists today (the skeleton, the notification type catalog and the recipient culture and time zone rules) and lists the rest under the sections that will hold it.

- Owns: the notification type catalog, the notification, delivery and in-app rows, user preferences and quiet hours, the inbox that makes its consumers idempotent, and the `notify` schema (not created yet; the first migration comes with the persistence task).
- Does not: send anything on its own initiative (it reacts to integration events), store a notification type table (D7: types are declared in code), or offer scheduling, cancellation, digests, push, SMS, webhooks, bulk sends, provider callbacks, attachments or fallback channels (D17).
- Talks to: `TemplateName.Modules.Auth.Contracts` only, never `TemplateName.Modules.Auth` (D1; `ModuleBoundaryTests` enforces it). It consumes Auth's integration events and, once when a notification is created, asks `IUserContactDirectory` for the recipient's address, name, language and time zone, which it snapshots on its own rows (D5). No other module references Notifications, so it has no `TemplateName.Modules.Notifications.Contracts` project yet; `INotificationTypeSource` and `NotificationTypeDefinition` move there when a module wants to declare its own types (D1).
- Consumers only write rows. A consumer records the event in `notify.InboxMessages` in the same save as the notification and its deliveries, so a repeated event has no effect (D3, [ADR 0018](../adr/0018-in-process-integration-events-with-inbox.md)); a separate delivery worker sends. Single-use links reach it as `ISecretProtector` ciphertext and are decrypted only in memory while an email is rendered ([ADR 0020](../adr/0020-secrets-in-cross-module-events.md)).

Code: `src/Modules/Notifications/TemplateName.Modules.Notifications/`. The host calls `AddNotificationsModule(configuration)` after `AddAuthModule`, maps `MapNotificationsEndpoints()` on the `/api/v1` group and `MapNotificationsHub()` on the application root; both map methods are empty until the endpoints and the SignalR hub are added. `AddNotificationsModule` registers the handlers and validators, the error messages (`NotificationsErrorMessages`), the `NotificationCatalog` singleton and the hosted service that validates it at start.

## Endpoints

<!-- Every endpoint, written as METHOD + full route (e.g. GET /api/v1/{module}/{resources}/{id:guid}), with its success status and error codes. -->

None.

`MapNotificationsEndpoints` and `MapNotificationsHub` exist so the host's wiring does not change when the inbox, preference and administration endpoints and the hub at `/hubs/notifications` arrive.

## Domain model

<!-- Aggregates, their invariants and life cycle. Draw state machines as a Mermaid stateDiagram-v2. -->

No aggregate yet. The enums below are stored as `tinyint`, so their numbers never change or get reused (`Domain/Notifications/`).

| Enum | Values |
|---|---|
| `NotificationChannel` | `Email = 1`, `InApp = 2` |
| `NotificationCategory` | `Security = 1`, `Account = 2`, `System = 3` |
| `NotificationPriority` | `Low = 1`, `Normal = 2`, `High = 3`, `Critical = 4` |

`Critical` ignores quiet hours (D11).

## Notification types

A notification type is declared in code as a `NotificationTypeDefinition` (`Application/Catalog/`): a `Code`, a category, a priority, the default channels, the mandatory channels (the recipient cannot switch these off), the template variables and the secret variables (a single-use link; never shown in a subject or an in-app text). `IsMandatory(channel)` and `IsConfigurable` (some default channel is not mandatory) are derived. A type's templates are embedded in the assembly that declares it (D8), so a type source says nothing about them.

An `INotificationTypeSource` returns the types it declares. `NotificationCatalog` (singleton) reads every registered source and validates it the first time it is used and again when the host starts (`NotificationCatalogStartupCheck`, a hosted service), so an invalid declaration stops the start. A failure throws `InvalidOperationException` naming the code and the source type. The rules:

| Rule | Detail |
|---|---|
| Code | Matches `^[a-z]+\.[a-z_]+\z` (`auth.password_changed`) and is at most 100 characters. |
| Unique | No code is declared twice, within one source or across sources; the message names both source types. |
| Channels | At least one default channel; every mandatory channel is also a default channel. |
| Variables | Variable and secret variable names match `^[a-z][a-z0-9_]*\z`, and no name is both. |

`Find(code)` returns a type or `null`, `All` lists them and `TemplateAssemblyOf(code)` returns the assembly of the source that declared the type. No source is registered yet, so the catalog is empty; Auth's types come with the templates in a later task.

## Recipient culture and time zone

Notifications use the recipient's saved settings, never the request culture or the operating system's (`CultureInfo.CurrentUICulture` is not read).

- `RecipientCulture.Resolve(locale)` returns the first of `en`, `ms`, `zh-Hans` found by walking the locale and its parents with `CultureInfo.GetCultureInfo(locale, predefinedOnly: true)`: `zh-CN` and `zh-SG` give `zh-Hans`, `ms-MY` gives `ms`, `en-GB` gives `en`. A null, blank, invalid or unsupported locale (`fr-FR`, `zh-Hant`) gives `en`.
- `TimeZoneResolver.Resolve(ianaId)` returns the zone `TimeZoneInfo.TryFindSystemTimeZoneById` finds (an IANA id such as `Asia/Kuala_Lumpur`; a runtime with ICU also maps Windows ids), else `UTC` with `IsFallback = true` so the caller can log that the id was unusable. Quiet hours use it (D11).

## Error codes

<!-- Every error code the module returns ({module}.snake_case), its HTTP status, its params and its English message. Messages live in Resources/{Module}ErrorMessages.resx with .ms.resx and .zh-Hans.resx (ADR 0009). -->

None.

The module returns no error code yet. `Resources/NotificationsErrorMessages` and its `.ms` and `.zh-Hans` files are in place and empty; every `notifications.*` code added later goes into all three (architecture tests check it).

## Events

<!-- Domain events (internal, dispatched through the module outbox) and integration events (published in *.Contracts), with their handlers. -->

None.

The module raises no events and has no outbox. It will consume Auth's integration events (`TemplateName.Modules.Auth.Contracts`) through `IIntegrationEventHandler<T>` consumers that write to the inbox (D2, D3).

## Configuration

<!-- Configuration sections and keys the module reads, with defaults. -->

None.

`AddNotificationsModule` takes the configuration for the sections later tasks add (`Notifications:Email`, `Notifications:Delivery`, `Notifications:Hub`).

## Data

<!-- The schema, its tables and indexes, and the migrations in order. -->

None.

The module will own the `notify` schema with its own `NotificationsDbContext`; see the data ownership table in [`docs/architecture/overview.md`](../architecture/overview.md#data-ownership). `Application/Abstractions/IUnitOfWork.cs` is the module's save abstraction, ready for the context.

## Background processing

<!-- Hosted services, outbox handlers and scheduled jobs the module runs. -->

`NotificationCatalogStartupCheck` (`Infrastructure/Catalog/`) touches the catalog once when the host starts, so an invalid type declaration fails the start. The delivery worker arrives with the delivery task.

## Observability

<!-- Log messages, ActivitySource and Meter names, and metrics the module emits. -->

None.

## Testing

<!-- Where the module's unit and integration tests live and what they cover. -->

Unit tests: `tests/TemplateName.UnitTests/Notifications/`. `NotificationCatalogTests` covers the catalog rules and the start-up check, `RecipientCultureTests` the locale mapping (including that the current UI culture is ignored) and `TimeZoneResolverTests` the IANA lookup and the UTC fallback. The Windows-id case accepts whatever the runtime resolves, so the tests give the same result on Windows and on Linux with ICU. Architecture tests include the module in `Assemblies.Modules` and `Assemblies.ErrorMessageResources`: module boundary, layering, naming, translation completeness and this document's outline.

## Changelog

<!-- Link to the changelog entries for this module's commit scope. -->

Changes to this module are the [`CHANGELOG.md`](../../CHANGELOG.md) entries with scope `notifications` (release-please prints them as **notifications:**). To list them from git: `git log --oneline -E --grep='^[a-z]+\(notifications\)!?:'`.
