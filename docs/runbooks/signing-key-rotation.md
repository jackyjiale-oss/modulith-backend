# Runbook: rotate the access-token signing key

Access tokens are ES256 JWTs signed with a key from configuration (ADR 0015). The settings accept several keys at once: the first entry with a private key signs, every entry validates tokens and is published at `GET /.well-known/jwks.json`. Rotation uses that, so no user is signed out. The reference is [Access tokens](../modules/auth.md#access-tokens), "Generating a key" and "Rotating". This page is the order of work.

## When

On the schedule your security policy sets, when a person who could read the key leaves, or at once when the private key may have leaked. After a leak, an old key that stays listed still validates tokens an attacker forged, so skip the wait of step 4 and retire it at once (step 5): every access token it signed then fails with 401, and clients get a new one at their next refresh, because refresh tokens are not signed JWTs.

## Who

Whoever owns the secret store and the deployment of the environment: the setting is a secret (`Auth:Jwt:SigningKeys`, user secrets or environment variables, never a file in the repository). No API permission is involved.

## Steps

1. **Generate the new key** on a trusted machine, straight into the secret store:

   ```bash
   openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt
   ```

   Choose a `KeyId` that says when it was made, for example `2026-12`; it must differ from every key already listed. The public half of the old key, for step 3, is `openssl pkey -pubout` of its private key.
2. **Publish the new key before it signs.** Required for any rolling or multi-instance deploy, and for any outside service that verifies tokens through the JWKS and caches it (the endpoint allows 5 minutes). Without it, an instance that already has the new list signs with a key that instances still on the old list do not know, and their requests fail with intermittent 401s until every instance is updated. Add the new key at the **end** of `Auth:Jwt:SigningKeys` with its `PrivateKeyPem`. It is published but does not sign, because an earlier entry still has a private key. Deploy to every instance and wait at least 5 minutes (the verifiers' cache). You may skip this step only for one instance with no outside verifier that you restart in one go: there is then a short downtime while it restarts, and tokens signed before it are checked against the new list in step 3, which still holds the old key, so no token fails.
3. **Make the new key current.** Put it **first** (index 0, with its `PrivateKeyPem`) and move the old key to index 1 with only its `PublicKeyPem`, so it stops signing but still validates and stays in the JWKS. As environment variables: `Auth__Jwt__SigningKeys__0__KeyId`, `Auth__Jwt__SigningKeys__0__PrivateKeyPem`, `Auth__Jwt__SigningKeys__1__KeyId`, `Auth__Jwt__SigningKeys__1__PublicKeyPem`. Deploy. Every instance must have the new list before any of them signs with the new key, which is why step 2 is required for a rolling or multi-instance deploy.
4. **Wait for the old tokens to expire**: at least `Auth:Jwt:AccessTokenLifetime` plus `Auth:Jwt:ClockSkew`, 10 minutes 30 seconds by default (and the JWKS cache of outside verifiers, 5 minutes). Refresh tokens are not signed JWTs and keep working; clients get a token signed by the new key at their next refresh.
5. **Retire the old key**: remove index 1 and deploy. The key is no longer listed or accepted.

## Verify

- `GET /.well-known/jwks.json` lists the keys you expect: after step 3 both `kid` values, after step 5 only the new one. Each entry carries only `kty` `EC`, `crv` `P-256`, `x`, `y`, `kid`, `alg` `ES256` and `use` `sig`.
- A fresh sign-in returns an access token whose header `kid` is the new `KeyId` (decode the first part of the token, which is base64url JSON).
- `GET /api/v1/auth/me` with a token signed by the old key answers 200 during step 4 and 401 after step 5.
- The start-up validation refused nothing: it names the entry (`Auth:Jwt:SigningKeys:{index}:...`) when a PEM is malformed, a `KeyId` is duplicated or the curve is not P-256, and never prints key material.

## Roll back

Before step 5, put the old key back at index 0 with its private key and move the new one down to a public-only entry; tokens signed by either still validate. After step 5, the old key can be listed again as a public-only entry if you still hold its public half and the tokens it signed matter. After a leak do not roll back: the leaked key stays retired. Revoking sessions does not stop an access token that is already out, because access tokens are not checked against the session on each request (they live at most the lifetime plus the skew); retiring the key is what ends them.
