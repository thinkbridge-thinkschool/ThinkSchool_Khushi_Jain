# Brief

## What I want built

Put the Angular app in `demo-app/` on Azure Static Web Apps and have it read my Week-1 Quotes API
using a managed identity. No client secret anywhere — not in the repository, not in app settings,
not in GitHub secrets.

## Target

The site is served over HTTPS from Static Web Apps, on the `*.azurestaticapps.net` hostname, with a
custom subdomain attached once its CNAME exists. Lighthouse must score 95 or better on performance,
accessibility, best practices and SEO.

## The API it must call

The Week-1 API is the `quotes-api` container app in resource group `rg-dev`, subscription
`e129e314-4dd4-4814-a559-14a2c569bf86`. Do not hard-code its URL. Resolve the ingress FQDN from
Azure at deployment time — the platform assigns it, and it changes if the container app is
recreated.

The endpoints the deployed pages use:

| Endpoint | Response |
|---|---|
| `GET /api/quotes?page={n}&size={n}` | `{ page, size, total, items: [{ id, author, text, ownerId, ownerActive }] }` |
| `GET /api/quotes/{id}` | `{ id, author, text, ownerId, isDeleted }`, or 404 with a zero-length body |
| `POST /api/auth/login` | `{ access_token, refresh_token, expires_in }`, or 401 with a zero-length body |
| `POST /api/auth/register` | 201 with the same token pair, 409 if the email is taken, 400 if invalid |

`page` must be 1 or more and `size` between 1 and 100; anything else answers 400 with a
`ValidationProblemDetails` keyed `page/size`. Pass those two query parameters through untouched and
let the API validate them.

Proxy those four and nothing else. `POST /api/quotes`, `DELETE /api/quotes/{id}` and
`/api/collections` must not be reachable through the proxy.

## Auth

The API accepts two kinds of bearer token and picks between them by looking at the issuer. Use the
Entra one.

Rules:

1. Get the token from a **managed identity**. Not a client secret, not a certificate, not a stored
   token, not the API's own `/api/auth/login`.
2. The browser must never hold a token for the API. A browser cannot hold a managed identity, so the
   token has to be acquired server-side and the browser has to talk to something that is not the API
   directly.
3. Whatever host you pick for that server-side hop must actually be able to read a managed identity
   at runtime. Prove it before declaring the wiring done — do not assume a host supports it.
4. The API validates the issuer as `https://login.microsoftonline.com/{tenant}/v2.0`, so the app
   registration must be set to issue v2 access tokens.
5. Check that the audience the API validates matches the `aud` the token actually carries. They are
   not the same string for v1 and v2 tokens.
6. Pulling the container image must also use the identity, not a registry password.
7. Deployment must not use a stored credential either. Use GitHub OIDC with a federated credential,
   and fetch anything short-lived at run time rather than storing it.

## What I want back

- The infrastructure as code, not portal clicks.
- A CI/CD workflow that builds and deploys both halves and then runs Lighthouse.
- A script that produces a verification log I can paste: the live URL, the Lighthouse score, the
  claims in the managed-identity token, evidence that the API *accepts* that token rather than just
  that it was sent, a real response from each endpoint, and proof that no secret exists in the
  repository or in app settings.
- Tell me anything in this brief that turns out to be wrong.
