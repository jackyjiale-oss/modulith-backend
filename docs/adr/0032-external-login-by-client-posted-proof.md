# 0032. External login by a client-posted proof, verified by one OpenID Connect verifier

- Status: Accepted
- Date: 2026-10-30
- Deciders: Lee Jia Le

## Context
The roadmap of 2026-10-20 asks for social login: a person signs in or signs up with an account they already have at Google, Apple, Microsoft, Facebook or another OpenID Connect provider, from a web SPA or a Flutter app, and ends up in the same session as a password login. Blueprint 9.7 sketched redirect-based `challenge` and `callback` routes; the review wins (`docs/blueprint-review.md`), and the design of this feature (the social login design, `docs/superpowers/specs/2026-10-30-social-login-design.md` in the template repository, which a generated project does not carry) replaces them.

The template is a JSON API with bearer tokens and no cookies (ADR 0015, ADR 0025). Its clients are a web SPA and a native app, and both can obtain a provider's ID token themselves, with the provider's own SDK or its web flow. Four questions follow: who runs the provider step and what the client sends, how a proof is verified, when an identity may be attached to an existing account, and what the second factor and step-up mean for a session the provider vouched for.

## Options considered

**Who runs the provider step (D1).**
1. **A backend redirect flow** (`challenge` and `callback`). The server keeps state for the redirect, needs a front-end handoff page and a cookie or a code exchange, and cannot serve a native SDK that returns a token to the app.
2. **Both flows.** Doubles the routes, the state and the tests.
3. **The client posts the proof.** An ID token with its nonce, or Facebook's access token, to one route per provider name. One route serves web and mobile, native SDKs work, and the backend keeps no redirect state.

**How a proof is verified (D2).**
1. **A class per provider.** Four near-copies of one verifier.
2. **ASP.NET Core's `AddGoogle` and `AddFacebook` handlers.** Cookie and redirect based, with no first-party Apple handler and a cookie scheme the template does not have.
3. **One OpenID Connect verifier with presets, and a small Facebook adapter.** Google, Apple and Microsoft all issue OIDC ID tokens, so each needs only an issuer, audiences and an email rule, and a generic entry (`Type: "oidc"`, an `Authority`) uses the same code. Facebook issues an ID token only in iOS Limited Login; on web and Android it issues an access token, which needs its own verifier (Graph `debug_token` and `/me`).

**When an identity may be attached to an existing account (D4).**
1. **Never link by email.** Safest, but a password user who taps "Sign in with Google" is told to sign in the old way first.
2. **Always link by the provider's email.** An account takeover when a provider's email is unverified or changes hands (the known nOAuth attack on Microsoft's `email` claim).
3. **Strict linking.** Automatic only when the provider's email is verified by that provider's own rule, the provider is trusted for email in configuration, **and** the local account's email is confirmed and the account has no confirmed MFA method (see Consequences). Otherwise the user signs in the usual way and links from settings after a step-up.

**The second factor (D5).**
1. **Trust the provider's own second factor.** Whoever enters the user's Google account bypasses the authenticator the user turned on here, and the template cannot know whether the provider enforced one.
2. **A per-provider switch.** A policy each project can get wrong.
3. **The challenge still applies.** An account with a confirmed method gets the MFA challenge after a social sign-in, as after a password.

**Whose tokens the client holds (D7).**
1. **Pass the provider's tokens through.** Two token systems, and no way to revoke a provider token.
2. **The backend issues every token.** The provider's proof is discarded after verification.

**Step-up for an account with no password (D8).**
1. **Require a password first.** A forced detour through forgot-password for a sensitive action.
2. **A fresh proof of one of the account's own linked identities.**

## Decision
We chose the third option of D1 and D2, strict linking, the challenge, the backend's own tokens and a provider proof for step-up (decisions D1, D2, D4, D5, D7 and D8 of the social login design; D3, D6, D9 to D12 are recorded there and in [Auth: External login](../modules/auth.md#external-login)). The working documents are that section and the [setup guide](../social-login-setup.md).

- **The client posts the proof (D1).** Five new routes under `/api/v1/auth/external`: `GET /providers`, `POST /{provider}/login`, `POST /{provider}/link`, `GET /logins` and `DELETE /logins/{id}`. `login` is anonymous, `auth-strict` and `no-store`, and answers the existing `LoginResult`: tokens, or the existing MFA challenge. A provider is a configuration name; a disabled or unknown one is the same 404 and is not listed.
- **One OIDC verifier with presets, no client secret (D2).** `OidcIdTokenVerifier` reads the provider's discovery document through the `oidc-discovery` resilience pipeline (ADR 0029) and checks the algorithm (`RS256`, `ES256`), signature, exact `iss`, exact `aud`, `exp` and `nbf`, an `iat` younger than `Auth:External:MaxTokenAge`, the nonce, and a subject. No client secret is stored for Google, Apple, Microsoft or a generic provider, so there is nothing to leak or rotate (Apple's rotating signed-JWT secret is avoided). The one secret, Facebook's app secret, comes from user secrets or the environment and is never in a file. A provider that cannot be reached is 503, never a bad token.
- **Strict linking (D4).** The identity is stored as `(Provider, Subject)` and found by that pair ever after, never by email (D3). An account is linked by a sign-in only when the identity has a usable email, the email equals the account's, the account's email is confirmed and the account has no confirmed MFA method; an unconfirmed account, one with two-step verification, or one that already has another identity at the provider, is 409 `auth.external_link_required`, told only to a caller who proved the address. A user has at most one login per provider.
- **The challenge applies (D5).** `auth.MfaChallenges` gains `FirstFactor` (`pwd` by default, or `fed`), so `POST /api/v1/auth/mfa/verify` records `fed otp mfa` or `fed rcvr mfa`. A trusted device never applies to a federated sign-in: no `trustedDeviceToken` is read at login, and `rememberDevice` is ignored for a `fed` challenge (ADR 0025: a device replaces the code, never the first factor).
- **The backend issues every token (D7).** The access and refresh tokens, `amr`, `auth_time`, revocation, rotation and reuse detection are the module's. The proof is verified once and discarded: no provider token is stored and no provider API is called afterwards.
- **Step-up by provider proof (D8).** `POST /api/v1/auth/step-up` gains the optional members `provider`, `idToken`, `accessToken` and `nonce`. An account with neither a password nor a confirmed MFA method steps up with a fresh proof of one of its own identities, and the session's `amr` becomes `fed` (a step-up replaces `amr`, ADR 0023). A password account keeps the password and an MFA account its code. A proof of an identity that is not the caller's is 400 `auth.step_up_identity_mismatch`; a refused provider proof is not counted toward the lockout.

## Consequences
- **`fed` is not an RFC 8176 value.** The RFC registers twenty `amr` values and none for a federated login, so a custom value is unavoidable, like `rcvr` (ADR 0023). Clients and resource servers that read `amr` must accept `fed`. The value is `AuthenticationMethods.Federated`.
- **Whether an email may be trusted depends on the provider.** Apple: `email_verified` true (a boolean or the string `"true"`). Google: `email_verified` true **and** the address is `@gmail.com` or the token carries `hd` (a Workspace account), because Google documents `email_verified` as authoritative only for those; for another address the verification may be stale (the mailbox may have changed hands), which with automatic linking would let a former owner take over a confirmed account. A Google user with such an address cannot sign up or link by email and links Google from settings instead; `googlemail.com` is not special-cased. Microsoft: only the optional claim `xms_edov` true, never the `email` claim, which Microsoft documents as unverified and mutable; the claim exists only if the project adds it to its app registration, and without it a Microsoft sign-up is refused with 403 `auth.external_email_unverified`. A generic provider: `email_verified`, and only with `TrustEmail` on. Facebook: never. `TrustEmail` (on for Google, Apple and Microsoft, off for generic entries, no effect for Facebook) lets a project turn the email off for a provider; turning it on for a provider that does not verify email reopens the takeover this decision closes. **An identity without a usable email cannot sign up or link by email**, so a first sign-in with an unlinked Facebook identity, or with a generic provider whose `TrustEmail` is off, is 403 `auth.external_email_unverified`: those users link the provider from settings after a step-up and sign in with it afterwards.
- **Microsoft's subject is `{tid}:{oid}`.** Its `sub` is pairwise per app registration, so the stable identifier across applications is `oid` within a tenant. The client must request `openid profile email`, because `oid` is issued only with `profile`; a token without `oid` or `tid` is refused.
- **Replay is bounded, not impossible.** A stolen ID token is accepted for at most `MaxTokenAge` plus the clock skew (5 minutes and 30 seconds by default) and its nonce once. The single-use check is strict on one instance (an in-process gate in front of `HybridCache`) and best effort across instances (shared through Redis, per instance without), like the permission cache's 30 seconds. A Facebook access token has no nonce and no age; it is bounded only by its own expiry and Facebook's revocation (see [Known limits](../modules/auth.md#known-limits)).
- **The proof is only as fresh as the client makes it.** The backend cannot tell a token the user just obtained from one the client held back inside `MaxTokenAge`; the nonce binds it to the request the client built, not to a moment the server chose. A server-issued nonce would need an extra route and server state, against D1.
- **A provider's metadata is a dependency of sign-in, not of the whole service.** A provider outage is 503 for that provider's routes; password login and every other provider work. A user whose only login is at a provider that is switched off or down cannot step up until it is back or they set a password through forgot-password.
- **An account with two-step verification is never linked automatically.** A link made by a sign-in is saved before the MFA challenge is answered, so it would attach the identity, for good, on the strength of the provider's email alone, while the manual link route asks an MFA account for a session that proved a code. Such an account answers 409 `auth.external_link_required` at its first social sign-in, before anything is saved; the user signs in with the password and a code and links the provider from settings, and every later social sign-in gets the challenge. The cost is one extra step for MFA users. An identity that is already linked keeps signing in, with the challenge.
- **No account merging.** Two existing accounts are never combined, and an email that matches an unconfirmed, MFA-protected or already linked account sends the user to the signed-in link route.
- **Presets can drift.** The four presets were read from each provider's live discovery document on 2026-10-30; the setup guide says to check them again when a provider changes, and a generic entry is the escape hatch. CI never calls a real provider: every verification path is tested against keys and metadata the tests generate, and the setup guide carries a manual checklist.
- **A backend redirect flow is not built.** A project that needs one adds routes later; the verifier is reusable.
- Follow-up actions: push and SMS channels and phone-number OTP login are their own plans; a trusted device never applies to a federated sign-in ([ADR 0025](0025-trusted-devices-bound-to-the-security-stamp.md)); step-up gains a provider proof ([ADR 0023](0023-step-up-through-auth-time-and-amr.md)).
