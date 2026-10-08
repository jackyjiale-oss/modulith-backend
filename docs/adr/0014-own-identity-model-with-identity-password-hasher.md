# 0014. Own identity model, with ASP.NET Core Identity used only for password hashing

- Status: Accepted
- Date: 2026-10-08
- Deciders: Lee Jia Le

## Context
The Auth module needs users, roles, sessions and verification codes, and a way to store passwords safely. ASP.NET Core Identity offers all of it as a package: `IdentityUser`, `UserManager`, EF Core stores and a `PasswordHasher`. The template's rules pull the other way:

- Business state changes go through aggregates that guard their invariants, raise domain events and save through the module's unit of work and outbox (ADR 0006, ADR 0007). `UserManager` mutates anonymous properties and saves on its own, so it bypasses all three.
- A module's `Domain` namespace may not depend on `Microsoft.AspNetCore.*` (architecture test). `IdentityUser` lives in `Microsoft.AspNetCore.Identity`, so it cannot be the domain's user.
- Login is by email only (D3) and a refresh-token family is the session (D2); Identity's model has a separate user name and no session concept.

Password hashing is the one part that is both security-critical and not worth rewriting.

## Options considered
1. Full ASP.NET Core Identity (`UserManager`, `SignInManager`, EF stores): least code, but the model and the unit-of-work rules above are lost, and the schema carries columns the template does not use.
2. Own aggregates plus Identity's `PasswordHasher<TUser>` (PBKDF2-HMAC-SHA512, 100 000 iterations, random salt, versioned format that can be upgraded on login), behind our own `IPasswordHasher` abstraction.
3. Own aggregates plus Argon2id (a native or third-party library): the stronger algorithm against GPU attacks, but an extra dependency, a native binary to ship and a licence to check.

## Decision
We chose option 2.

- The module owns the `User`, `Role`, `UserSession` and `VerificationCode` aggregates (`Domain/`), with their invariants, events and repositories. No `UserManager`, no Identity EF stores, no `IdentityUser`.
- Passwords are hashed with `Microsoft.AspNetCore.Identity.PasswordHasher<T>`. It ships in the ASP.NET Core shared framework (`Microsoft.Extensions.Identity.Core`), so the module adds no package for it. Only the module's `Infrastructure` touches it, behind the internal `IPasswordHasher`; the domain never sees it.
- Argon2id is rejected for now because of the extra native dependency. The `IPasswordHasher` abstraction keeps it swappable: a new implementation can verify old PBKDF2 hashes and rehash on login without touching any handler.
- Decisions D1 (own model), D2 (no `FamilyId`) and D3 (email-only login) of Plan 2 follow from this.

## Consequences
- Positive: aggregates, domain events and the outbox work for users as for any other module; a vetted, Microsoft-maintained hasher with a versioned format; no unused Identity tables.
- Negative / trade-offs accepted: we write and maintain the user, role and session code ourselves, including lockout and password history, and Identity's ecosystem (external-login helpers, scaffolded UI) is not available.
- Follow-up actions: revisit Argon2id when a maintained, MIT or Apache-licensed managed implementation is available or when the threat model asks for it; the swap is one class and a rehash-on-login rule.
