# 0015. ES256 access tokens signed with keys from configuration

- Status: Accepted
- Date: 2026-10-08
- Deciders: Lee Jia Le

## Context
The API issues short-lived access tokens and validates them on every request. The token format, the signing algorithm and where the keys live decide how safely a key can be rotated and how much an attacker gains from a leaked key or a forged header. Other services and the template's clients must be able to verify tokens without sharing a secret.

## Options considered
1. HS256 with a shared secret: simplest, but every verifier holds the signing secret, and the well-known "algorithm confusion" attacks (an HS256 token signed with a public key's bytes) become possible if the validator accepts more than one algorithm.
2. RS256 with RSA keys: widely supported, but larger keys and signatures and slower signing.
3. ES256 (ECDSA, P-256) only, with keys supplied through configuration and the public keys published as a JWKS.
4. A `SigningKeys` table with automatic rotation: convenient, but it needs a protected private-key store and a job; it is deferred (D5).

## Decision
We chose option 3.

- **Algorithm:** ES256 only. The validator allow-lists `ES256`, so `none`, `HS256` and every other algorithm are rejected. It validates `iss`, `aud`, `exp` and `nbf` with a clock skew of 30 seconds, and `MapInboundClaims = false` keeps claim names as issued. The token carries no permission list and no personal data (ADR 0016).
- **Keys:** `Auth:Jwt:SigningKeys` is a list of keys, the active key first. Each entry has a `kid` and a PEM private key; a retired key may keep only its public part. Secrets come from user-secrets, environment variables or a secret store, never from files in the repository.
- **Rotation:** add the new key at the head of the list and keep the previous key's public part for at least `AccessTokenLifetime + ClockSkew` (10 minutes and 30 seconds by default), so tokens signed with it stay valid until they expire; then remove it.
- **Development and Testing:** when no key is configured, an ephemeral key is generated at start. In any other environment the application refuses to start without a key, so a deployment can never run with a throw-away key.
- **JWKS:** the public keys are served at `/.well-known/jwks.json`, so other services verify tokens without a secret.
- **Revocation window (D9):** an access token is not checked against session state on each request, so after a logout or a revoked session its access token works until it expires, 10 minutes at most. Refresh stops at once. Checking the session on every request would make the JWT stateful and cost a lookup per request; the short lifetime is the trade-off.

## Consequences
- Positive: no shared secret between the issuer and verifiers; small tokens and keys; a fixed algorithm closes the algorithm-confusion class of attacks; rotation needs a configuration change and no code change.
- Negative / trade-offs accepted: rotation is manual until a key table and job exist; a stolen access token stays usable for up to 10 minutes after logout; the active private key sits in configuration, so the secret store is what protects it.
- Follow-up actions: generate and store a production key before the first deployment (Plan 2, Part C); automate rotation and storage with a certificate or Key Vault in Plan 6.
