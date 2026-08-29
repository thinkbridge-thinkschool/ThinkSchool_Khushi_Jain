# Day 17 — Deploy to Azure Static Web Apps

The `demo-app` Angular client live on Azure Static Web Apps, reading my Week-1 Quotes API with a
managed identity. No client secret anywhere.

**Live: https://proud-sky-0fa504d0f.7.azurestaticapps.net**

My brief to the agent is [BRIEF.md](BRIEF.md). The verification log is
[verification.log](verification.log).

## Why there is a backend at all

A browser cannot hold a managed identity. It is handed to Azure compute over a local endpoint the
platform provides; there is no such thing in a page. So the SPA cannot call the API with a
managed-identity token, and the requirement forces a server-side hop.

```
browser ──/api/*──▶ Static Web Apps ──linked backend──▶ quotes-bff container app
                                                             │  user-assigned identity
                                                             │  Bearer <Entra v2 token>
                                                             ▼
                                                        quotes-api container app
```

Static Web Apps proxies `/api/*` to the linked container app, so the call is same-origin and the
Angular code did not change: `API_BASE_URL` stays empty, exactly as it is in development behind the
dev-server proxy.

The backend is [bff/server.js](bff/server.js). It asks the platform for a token for
`api://467f0295-…/.default` and attaches it. It forwards only `GET /api/quotes`,
`GET /api/quotes/{id}` and `POST /api/auth/{register,login,refresh,logout}` — anything else is refused there
rather than passed on, so it cannot be used as an open relay. Sign-in is forwarded without the
managed-identity token, because that is the user's own credential flow.

## Where the secrets aren't

| Thing that usually needs a secret | What is used instead |
|---|---|
| Backend → Quotes API | User-assigned managed identity `id-quotes-bff` |
| Container app → registry | The same identity, `AcrPull`; no registry password |
| API → its JWT signing key | Key Vault, read by the API's own identity |
| GitHub Actions → Azure | OIDC federated credential |
| Actions → SWA deploy token | Fetched at run time with that login, masked, never stored |

The backend's four settings are the API URL, the scope, an on/off flag and the identity's client id.
All four are public values. `verification.log` section 8 shows them, shows the container app has no
secrets at all, and shows the registry pull uses an identity rather than a `passwordSecretRef`.

## Verify it

```bash
bash day17_swa_deploy/verify.sh
```

It rewrites `verification.log`: what is deployed, the site and its security headers, the claims in
the managed-identity token, proof the API accepts it, a real page of quotes, every failure state,
the Lighthouse run, and a scan of the repository and app settings for anything secret-shaped.

## Results

| Requirement | Result |
|---|---|
| Live URL | `https://proud-sky-0fa504d0f.7.azurestaticapps.net` — 200 in 0.53s |
| Lighthouse performance | 100 |
| Lighthouse accessibility | 100 |
| Lighthouse best practices | 100 |
| Lighthouse SEO | 100 |
| Managed-identity token | `aud` `467f0295-…`, `iss` `…/5aaf8f39-…/v2.0`, `appid` `4e85f37c-…` |
| API accepts that token | `POST /api/quotes` answers **403**, not 401 |
| Secrets in repo or app settings | none |
| Custom domain | not attached — no subdomain supplied yet |

The 403 is the important line. A 401 would mean the token was rejected. A 403 means the API
validated its signature, issuer, audience and lifetime, and then refused the write for scope — so
the token is genuinely being accepted.

States exercised, all in the log: detail 200, unknown id 404, non-numeric id refused at the proxy
404, `page=0&size=0` 400, wrong credentials 401, and an unauthenticated write against the API 401.

## What I made it fix

**It picked a host that cannot hold a managed identity.** The first build put the proxy in Static
Web Apps' managed functions. That deployed cleanly and the read path returned 200, which looked like
success — but every response carried `x-managed-identity-token: unavailable`. Static Web Apps can be
given a system-assigned identity, and it is used for Key Vault references, but the managed-functions
runtime does not expose it to function code. The whole point of the exercise was silently missing
while the site appeared to work.

I moved the proxy to a container app, which does expose an identity, and made the header prove it
rather than trusting the deployment.

**Then the audience did not match.** With v2 access tokens Entra issues `aud` as the bare client id,
`467f0295-…`, but the API was configured to validate `api://467f0295-…`. Every call would have been
rejected. I only caught it because the token dump prints `aud` — the read endpoints are anonymous,
so they answered 200 either way and hid the problem. That is why the write probe exists: it is the
only call that distinguishes "token accepted" from "token ignored".

Two smaller things: it hard-coded the container app's ingress FQDN, which the platform assigns and
which does not survive the app being recreated, so the Bicep now looks it up; and it wrote
`string(introspectionEnabled)` in Bicep, which emits `True`, against a comparison to `'true'`.

## Sign-up and durable storage

Anyone can create their own account from the sign-in card — `POST /api/auth/register` takes an email
and a password of at least 8 characters, hashes it with BCrypt, and issues the same token pair as
login so signing up logs you straight in. A repeated email answers 409.

Accounts have to outlive the container for that to mean anything. The API previously wrote its
SQLite file to the container filesystem with no volume, so every restart erased every account. An
Azure Files share is now mounted at `/data` and the connection string points there.

SQLite does not work on an SMB share out of the box: it needs POSIX byte-range locks that Azure
Files does not provide, and the container hung on startup with no error. The mount carries
`nobrl` (plus `uid`/`gid` matching the non-root user the .NET image runs as) to disable that
locking, which is what makes it start.

Verified by restarting the container and checking the data was still there: 20 quotes and a working
sign-in, where before the restart emptied everything.

## What breaks if the API changes

- **The audience or tenant changes.** Every call becomes 401. The audience lives in one setting on
  the container app, so the fix is one setting and a restart, but nothing warns me first — the token
  is still issued, and it is the API that rejects it.
- **The registration drops back to v1 tokens.** Every call becomes 401, and confusingly so: the
  issuer becomes `https://sts.windows.net/…`, the API's scheme selector no longer recognises it as
  an Entra token, and it is handed to the internal JWT handler, which fails on the signature. The
  symptom looks like a bad key rather than a bad issuer.
- **`GET /api/quotes` changes its envelope.** The proxy passes the body through untouched, so it
  breaks in the Angular layer, not here — `QuotesPage` in `demo-app/src/http.ts`.
- **A route is renamed.** The proxy allowlists `/api/quotes`, `/api/quotes/{id}` and the three auth
  actions by shape. A renamed route returns 404 from the proxy, before the API is ever called.
- **The API starts requiring authorization on the reads.** Nothing breaks. That is the point of
  attaching the token even though those endpoints are anonymous today.

## Known gaps

- **The write path cannot work under a managed identity.** `can-edit-quotes` in
  `QuotesApi/Extensions/InfrastructureExtensions.cs` reads only `scope`/`scp`, which are
  delegated-token claims; an app-only token carries `roles`. The token dump shows both as null.
  Making writes work would mean a `quotes.write` app role on the API registration, assigned to the
  identity, and a policy that accepts `roles` as well as `scp` — a change to shipped Week-1 code
  that this task does not ask for.
- **The API runs in Development** so its starter account seeds on a fresh database.
  `appsettings.Development.json` would raise EF command logging to Debug, which with sensitive-data
  logging would put emails and hashes in the logs, so environment variables force those loggers back
  to Warning.
- **Registration is open.** Anyone can create an account and add quotes to the shared list. That is
  deliberate for a demo anyone can open, but there is no email verification and no rate limiting.
- **The file share mount uses an account key.** Container Apps mounts Azure Files with a storage
  account key held in the environment, so the API has one stored secret. The Day 17 backend and its
  call to the API stay secret-free; this is the API's storage, not its auth.
- **No custom domain.** Everything is in place for one; it needs a subdomain and its CNAME.
