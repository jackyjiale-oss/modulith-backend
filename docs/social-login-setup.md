# Social login setup

How to switch on sign-in with Google, Apple, Microsoft, Facebook or another OpenID Connect provider: what to create at each provider, what goes into `Auth:External`, what the client sends, and what to check by hand before a release. The behaviour of the routes is in [Auth: External login](modules/auth.md#external-login), every setting in [Auth: Configuration](modules/auth.md#configuration) and the decision in [ADR 0032](adr/0032-external-login-by-client-posted-proof.md).

**How it works, in one paragraph.** The client (a web SPA or a Flutter app) runs the provider's own sign-in and gets an ID token, or for Facebook an access token. It posts that proof, with a nonce it generated, to `POST /api/v1/auth/external/{provider}/login`. The backend verifies the proof against the provider's published keys, finds or creates the user and answers like a password login: its own access and refresh tokens, or the MFA challenge. The provider's token is discarded; nothing is kept or called afterwards. There is no redirect URL on the backend, so there is nothing to register at a provider as a callback for the API (the redirect URIs belong to the client's own flow).

**CI never calls a real provider.** Every verification path is tested against keys and discovery documents the tests generate. The only check of a real provider is the [manual checklist](#manual-checklist) below, run by a person before a release that enables one. The provider facts in this guide (issuers, key addresses, algorithms, claims) were verified from each provider's live discovery document and documentation on 2026-10-30 (design spec, section 12) and **must be re-checked when a provider changes**: a preset that drifts from the provider's metadata makes every sign-in of that provider fail with 401 `auth.invalid_external_token`, and a generic entry is the escape hatch.

## What each provider needs

| Provider | The client posts | `ClientIds` (the accepted `aud`) | Email usable for sign-up and linking when |
|---|---|---|---|
| Google | `idToken` and `nonce` | one OAuth client ID per platform that sends tokens (web, iOS, Android) | `email_verified` is true |
| Apple | `idToken` and `nonce` | the Services ID (web) and the bundle ID (native app) | `email_verified` is true |
| Microsoft | `idToken` and `nonce` | the Application (client) ID of the app registration | the optional claim `xms_edov` is true |
| Facebook | `accessToken` (web, Android), or `idToken` and `nonce` (iOS Limited Login) | not used; `AppId` and `AppSecret` instead | never |
| Generic (`Type: "oidc"`) | `idToken` and `nonce` | the client IDs of your registration | `email_verified` is true and `TrustEmail` is on |

A nonce is mandatory with an ID token (16 to 256 characters). The client generates it, puts it into the provider request so that it ends up as the token's `nonce` claim, and posts the same value. Where a provider's SDK hashes the nonce first (Apple's native SDK does), the token carries the lower-case hex SHA-256 of what you posted, and the backend accepts that too. A flow or an SDK that cannot place a client nonce into the ID token cannot be used with this backend.

An ID token must be young: its `iat` may be at most `Auth:External:MaxTokenAge` old (5 minutes by default, 1 minute to 1 hour), so the client posts it right after the provider returns it. Each nonce works once.

## Switching a provider on and off

Every provider ships `Enabled: false`. A project turns one on in configuration (an `appsettings` override, user secrets or environment variables) and restarts: the provider catalog is read once at start-up.

- **On.** The provider appears in `GET /api/v1/auth/external/providers` (`name`, `displayName`, `type`; never a client ID or a secret) and its `login` and `link` routes work. An enabled provider with a missing setting stops the host at start-up with a message that names the key and never quotes a value.
- **Off.** The provider is not listed, and `login`, `link` and a step-up with its proof answer 404 `auth.external_provider_not_found`, exactly like an unknown name. Nothing is validated for a disabled entry. Users' stored logins stay and can still be listed and unlinked. Nobody is locked out of the account: forgot-password still lets them set a password. A user whose **only** sign-in is at the switched-off provider cannot step up until it is back or they have a password.
- **A name of your own** is a generic OpenID Connect entry (`Type: "oidc"`); the name is a route segment, 1 to 64 letters, digits, `-` or `_` (starting with a letter or digit), compared without regard to case.

`Auth:External:AllowSignUp` (default `true`) decides whether a first social sign-in with no matching account creates one; an invite-only project sets it `false` and users link a provider from their settings instead (403 `auth.external_sign_up_disabled` otherwise).

## Configuration example

No secret appears in a file. The values below are placeholders.

```json
{
  "Auth": {
    "External": {
      "AllowSignUp": true,
      "MaxTokenAge": "00:05:00",
      "Providers": {
        "Google": {
          "Enabled": true,
          "ClientIds": [ "<web-client-id>.apps.googleusercontent.com", "<ios-client-id>.apps.googleusercontent.com" ]
        },
        "Microsoft": { "Enabled": true, "ClientIds": [ "<application-client-id>" ], "TenantId": "common" },
        "Apple": { "Enabled": true, "ClientIds": [ "<services-id>", "<bundle-id>" ] },
        "Facebook": { "Enabled": true, "AppId": "<app-id>" },
        "Okta": {
          "Enabled": true,
          "Type": "oidc",
          "DisplayName": "Okta",
          "Authority": "https://<your-org>.okta.com",
          "ClientIds": [ "<client-id>" ],
          "TrustEmail": false
        }
      }
    }
  }
}
```

The same keys work as environment variables with a double underscore (`Auth__External__Providers__Google__Enabled=true`, `Auth__External__Providers__Google__ClientIds__0=...`).

**The one secret.** Facebook's app secret is never in a file and the shipped `appsettings.json` has no key for it:

```bash
dotnet user-secrets set "Auth:External:Providers:Facebook:AppSecret" "<app-secret>" --project src/Host/TemplateName.Api
```

In a deployment set `Auth__External__Providers__Facebook__AppSecret` from the platform's secret store. Google, Apple, Microsoft and generic providers have no secret at all: the backend only verifies signatures with the provider's public keys, so there is no client secret to store or rotate (Apple's rotating signed-JWT secret is not needed).

## Google

1. In the Google Cloud console, create an OAuth consent screen, then one **OAuth client ID** for each platform that will sign users in: a *Web application* client for the SPA, an *iOS* client, and for Android the client your app requests an ID token for.
2. Put **every client ID whose tokens your clients can send** into `ClientIds`. The `aud` of a Google ID token is the client ID the token was requested for: on Android that is usually the web client ID the app passes as its server client ID; on iOS it is the iOS client ID unless you set a server client ID. A token for a client ID that is not listed is refused.
3. Issuer: the backend accepts `https://accounts.google.com` and the bare `accounts.google.com` (Google documents both). Keys: `https://www.googleapis.com/oauth2/v3/certs`. Signing algorithm `RS256`.
4. Email: `email_verified` (a boolean, or the string `"true"`) must be true; `TrustEmail` defaults on.

## Apple

1. In the Apple developer account, enable *Sign in with Apple* for the **App ID** (its bundle ID is what a native app's tokens carry as `aud`) and, for the web, create a **Services ID** with the web domain and return URL of your client. Put **both** the Services ID and the bundle ID into `ClientIds` (leave out the one you do not use).
2. **The nonce is hashed on native.** The native SDK takes the SHA-256 (lower-case hex) of your raw nonce as its request nonce, so the token's `nonce` claim is the hash. Post the **raw** nonce; the backend accepts a token whose claim equals the raw nonce or its lower-case hex SHA-256. The web flow passes the nonce through unchanged.
3. **Apple gives the name and, in some cases, the email only once.** The user's name is never inside the ID token: Apple sends it to the client, on the first authorization only, so the client must pass it as `displayName` in the same request (otherwise the backend uses the token's `name`, else the part of the email before `@`). Apple may also disclose the email only at the first authorization of an app. A later token for an identity that is already linked needs no email (the login is found by `sub`), but a token with no email for an identity that is not yet linked is refused with 403 `auth.external_email_unverified`. A user who must start over removes the app under *Sign in with Apple* in their Apple ID settings, so Apple treats the next authorization as a first one.
4. **Private relay.** A user may choose *Hide My Email*; Apple then provides an address at `privaterelay.appleid.com`. The backend treats it as the user's email (it is a verified address), so such a user is a different person from the password account with their real address: they get a new account, not a link. To attach Apple to an existing account the user links from settings, signed in. To deliver mail to relay addresses, register your sending domain or address in the Apple developer account (*Configure Sign in with Apple for Email Communication*).
5. Issuer `https://appleid.apple.com`, keys at `https://appleid.apple.com/auth/keys`, `RS256`. `email_verified` may be a boolean or a string; `TrustEmail` defaults on.

## Microsoft

1. In Microsoft Entra, register an application. Choose the supported account types that match your `TenantId` (below). Add each client as a platform (single-page application, iOS or Android); the `aud` of the ID token is the **Application (client) ID**, which goes into `ClientIds`.
2. **Request `openid profile email`.** Microsoft issues the stable object ID (`oid`) only with the `profile` scope, and the backend identifies a Microsoft user by `tid` and `oid` (their `sub` differs per app registration). A token without `tid` or `oid` is refused. The client must sign in through the v2.0 endpoint (MSAL does), because the backend accepts only the v2.0 issuer `https://login.microsoftonline.com/{tid}/v2.0` for the token's own tenant.
3. **Add the optional claim `xms_edov`.** Microsoft's `email` claim is documented as unverified and mutable, so the backend never trusts it. The only usable signal is the optional claim `xms_edov` (the email's domain owner is verified). In the app registration open *Token configuration*, *Add optional claim*, token type **ID**, and add `xms_edov` (it needs the `email` claim as well; if the portal offers to turn on the Microsoft Graph `email` permission for it, accept). **Without it a first Microsoft sign-in has no usable email and is refused with 403 `auth.external_email_unverified`.** An already linked Microsoft login signs in without it.
4. **Choose the tenant** in `TenantId`: `common` (the default; work, school and personal accounts), `organizations` (work and school accounts of any tenant, never personal accounts), `consumers` (personal accounts only) or a tenant GUID (only that tenant). A domain name is refused at start-up, because a token names its tenant by GUID. Microsoft personal accounts have the `tid` `9188040d-6c67-4c5b-b112-36a304b66dad`.
5. Discovery document `https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration` (through the configured tenant), keys at `https://login.microsoftonline.com/common/discovery/v2.0/keys`, `RS256`. `TrustEmail` defaults on.

## Facebook

1. In Meta for Developers, create an app and add *Facebook Login*. The **App ID** goes into `AppId`; the **App Secret** goes into user secrets or the environment only (see above). Without both the host refuses to start with the provider enabled.
2. **Two kinds of proof.** On the web and on Android the client gets an **access token** and posts it as `accessToken` (no nonce). The backend asks Graph `debug_token` with `AppId|AppSecret` (valid, issued to this app, not expired) and `/me` with an `appsecret_proof`. On iOS, **Limited Login** returns an **ID token** with a nonce instead; post it as `idToken` and `nonce`. The backend verifies it against Facebook's keys, with the `AppId` as audience; Graph is not used for it.
3. **Facebook's email is never usable.** Facebook gives no signal that the address is verified, so the backend never uses it to find or link an account. `TrustEmail` has no effect for Facebook. The consequence is plain: **a first sign-in with an identity that is not yet linked is refused with 403 `auth.external_email_unverified`, so Facebook cannot create an account.** A user signs in the usual way and links Facebook from settings (`POST /api/v1/auth/external/facebook/link`, after a step-up); from then on Facebook signs in.
4. **Freshness.** An access token has no nonce and no age check of its own: it works until it expires (a long-lived token about 60 days; an `expires_at` of 0 never expires) or Facebook revokes it. Use a Limited Login ID token where freshness matters, especially for the step-up of a Facebook-only user (see [Known limits](modules/auth.md#known-limits)).
5. Use of the app by people other than its developers needs Meta's app review and the app in live mode; that is Meta's process, not a setting here.
6. Facebook's metadata address ends with a slash (`https://www.facebook.com/.well-known/openid-configuration/`) and the backend fetches it exactly so. Issuer `https://www.facebook.com`, keys at `https://www.facebook.com/.well-known/oauth/openid/jwks/`, `RS256`.

## A generic OpenID Connect provider

Any provider that issues OIDC ID tokens signed with `RS256` or `ES256` (Okta, Auth0, Keycloak and others) works as an entry with `"Type": "oidc"`:

- `Authority`: the issuer's absolute `https` URL, without credentials, query or fragment. The backend reads `{Authority}/.well-known/openid-configuration`, and the `Authority` is also the **exact** accepted `iss`, so write it as the provider's discovery document does: some providers (Auth0, for one) end the issuer with a slash, others do not.
- `ClientIds`: the client IDs of your registration, the accepted `aud` (exact, case-sensitive).
- `DisplayName` (optional): the label for a sign-in screen; it defaults to the entry's name.
- `TrustEmail` is **off by default.** Turn it on only for a provider that sets `email_verified: true` for addresses it has really verified: with it on, a token from that provider whose email matches a confirmed account links to that account automatically, and a provider that does not verify email would let anyone take over an account by registering its address there. With it off, an unlinked identity has no usable email and a first sign-in is refused with 403 `auth.external_email_unverified` (users link from settings).
- The token's `iat` must be a whole number of seconds (RFC 7519 allows a fraction; a token with one is refused), and its `sub` 1 to 256 characters.

## Manual checklist

Run this against a deployed or local host with each provider you enable, before the first release that uses it and again whenever a provider changes its console, SDK or tokens. None of it is automated and none of it runs in CI.

- **Every enabled provider.**
  - `GET /api/v1/auth/external/providers` lists exactly the enabled providers and nothing else.
  - A sign-in with a new person creates an account (200, tokens) and `GET /api/v1/auth/me` shows the provider's email and the right display name; a second sign-in signs in the same account.
  - Posting the same ID token again is 401 `auth.invalid_external_token` (single-use nonce); posting one older than `MaxTokenAge` is 401.
  - A person whose account has two-step verification gets the challenge, and `POST /api/v1/auth/mfa/verify` finishes it; the access token's `amr` reads `fed otp mfa`.
  - Link and unlink from settings, after a step-up, with a second account at the same provider refused (409 `auth.external_provider_already_linked`).
  - Switch the provider off and restart: it disappears from the list and `login` answers 404.
- **Google.** A sign-in from each platform you ship (web, iOS, Android); each client ID is in `ClientIds`.
- **Apple.** A round trip with the **Services ID** on the web and with the **bundle ID** in the native app. On native, confirm the nonce posted is the raw value while the token carries its hash. Sign in once with *Hide My Email* and once with the real address; check the display name arrives through `displayName`.
- **Microsoft.** One **personal** account and one **work or school** account, each signing up on a fresh database: check the token has `tid` and `oid` (the `profile` scope is requested), whether it carries `xms_edov` (a personal account may not, in which case sign-up is refused with 403 `auth.external_email_unverified` by design), the issuer, and that your `TenantId` accepts and refuses the accounts you expect (a personal account under `organizations`, a work account under `consumers`).
- **Facebook.** Link Facebook to an existing account and sign in with it (a first sign-in of an unlinked Facebook identity is refused by design); with an access token (web or Android) and, on iOS, with a Limited Login token; the app is in live mode and has passed app review for the permissions you need.
- **One generic provider.** The `Authority` matches the discovery document's `issuer` exactly, `ClientIds` matches the token's `aud`, and the email rule behaves as you set `TrustEmail`.

## When a provider changes

Re-read the provider's discovery document and token documentation when it announces a change to issuers, keys, claims or SDK behaviour, and run the checklist for that provider again. The four named providers' issuers, key addresses and email rules are fixed in the module (`ExternalProviderPresets`); a preset that has drifted is fixed in code, while a generic entry is fixed in configuration.
