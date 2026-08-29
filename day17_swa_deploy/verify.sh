#!/usr/bin/env bash
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
set -a
. "$here/deploy.env"
set +a

cd "$here"
exec > >(tee verification.log) 2>&1

echo "=============================================================="
echo " Day 17 verification -- $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
echo "=============================================================="

az account set --subscription "$AZURE_SUBSCRIPTION_ID"

site="https://${SWA_CUSTOM_DOMAIN:-$SWA_DEFAULT_HOSTNAME}"
api=$(az containerapp show -g "$AZURE_RESOURCE_GROUP" -n "$QUOTES_API_CONTAINER_APP" --query 'properties.configuration.ingress.fqdn' -o tsv)
bff=$(az containerapp show -g "$AZURE_RESOURCE_GROUP" -n "$QUOTES_BFF_CONTAINER_APP" --query 'properties.configuration.ingress.fqdn' -o tsv)
principal=$(az identity show -g "$AZURE_RESOURCE_GROUP" -n "$QUOTES_BFF_IDENTITY" --query principalId -o tsv)
client=$(az identity show -g "$AZURE_RESOURCE_GROUP" -n "$QUOTES_BFF_IDENTITY" --query clientId -o tsv)

echo
echo "--- 1. what is deployed ------------------------------------"
echo "live site         : $site"
echo "week-1 api        : https://$api"
echo "managed-identity  : https://$bff  (container app)"
echo "bff identity      : $QUOTES_BFF_IDENTITY"
echo "  clientId        : $client"
echo "  principalId     : $principal"
az staticwebapp backends show -n "$SWA_NAME" -g "$AZURE_RESOURCE_GROUP" --query "[].{backend:name,state:provisioningState,region:region}" -o table

echo
echo "--- 2. the site loads --------------------------------------"
curl -sS -o /dev/null -w "GET %{url_effective} -> %{http_code} in %{time_total}s\n" "$site/"
curl -sS -o /dev/null -w "GET %{url_effective} -> %{http_code} (deep link, SPA fallback)\n" "$site/list-detail"
echo "security headers:"
curl -sSI "$site/" | grep -iE 'content-security-policy|strict-transport-security|x-content-type-options|referrer-policy' | cut -c1-110

echo
echo "--- 3. the managed-identity token --------------------------"
curl -sS "$site/api/token-check"
echo
echo "the API validates Entra:Audience=$QUOTES_API_AUDIENCE and issuer .../v2.0"

echo
echo "--- 4. the API accepts that token --------------------------"
echo "POST /api/quotes carrying the managed-identity token:"
curl -sS "$site/api/write-probe"
echo
echo "403 = authenticated, refused for scope. 401 would mean the token was rejected."

echo
echo "--- 5. GET /api/quotes through the identity ----------------"
curl -sS -D .headers -o .quotes.json "$site/api/quotes?page=1&size=5"
grep -iE '^HTTP|x-managed-identity-token' .headers
head -c 400 .quotes.json
echo
first=$(grep -o '"id":[0-9]*' .quotes.json | head -1 | cut -d: -f2)

echo
echo "--- 6. states exercised ------------------------------------"
[ -n "${first:-}" ] && curl -sS -o /dev/null -w "GET  /api/quotes/$first        -> %{http_code}  (detail)\n" "$site/api/quotes/$first"
curl -sS -o /dev/null -w "GET  /api/quotes/999999        -> %{http_code}  (not found)\n" "$site/api/quotes/999999"
curl -sS -o /dev/null -w "GET  /api/quotes/abc           -> %{http_code}  (refused at the proxy)\n" "$site/api/quotes/abc"
curl -sS -o /dev/null -w "GET  /api/quotes?page=0&size=0 -> %{http_code}  (validation)\n" "$site/api/quotes?page=0&size=0"
curl -sS -o /dev/null -X POST -H 'Content-Type: application/json' -d '{"email":"nobody@example.com","password":"wrong"}' -w "POST /api/auth/login bad creds -> %{http_code}  (failed sign-in)\n" "$site/api/auth/login"
curl -sS -o /dev/null -X POST -H 'Content-Type: application/json' -d '{"author":"x","text":"y"}' -w "POST https://$api/api/quotes no token -> %{http_code}  (unauthenticated)\n" "https://$api/api/quotes"

echo
echo "--- 7. lighthouse ------------------------------------------"
npx -y lighthouse@12 "$site" --preset=desktop --output=json --output-path=./lighthouse.json --chrome-flags="--headless=new --no-sandbox" --quiet >/dev/null 2>&1
python -c "
import json, io
d = json.load(io.open('lighthouse.json', encoding='utf-8'))
print('url:', d['finalDisplayedUrl'])
for c in d['categories'].values():
    print('  %-16s %d' % (c['title'], round(c['score'] * 100)))
print('  console errors  ', len(d['audits']['errors-in-console'].get('details', {}).get('items', [])))
"

echo
echo "--- 8. no secret in app settings ---------------------------"
echo "bff container app environment:"
az containerapp show -g "$AZURE_RESOURCE_GROUP" -n "$QUOTES_BFF_CONTAINER_APP" --query "properties.template.containers[0].env[].{name:name,value:value}" -o table
echo "bff secrets:"
az containerapp show -g "$AZURE_RESOURCE_GROUP" -n "$QUOTES_BFF_CONTAINER_APP" --query "properties.configuration.secrets" -o tsv
echo "(empty above means no secret is stored on the container app)"
echo "static web app settings:"
az staticwebapp appsettings list -n "$SWA_NAME" -g "$AZURE_RESOURCE_GROUP" --query "properties" -o json
echo "registry pull uses:"
az containerapp show -g "$AZURE_RESOURCE_GROUP" -n "$QUOTES_BFF_CONTAINER_APP" --query "properties.configuration.registries[].{server:server,identity:identity,passwordSecretRef:passwordSecretRef}" -o table

echo
echo "--- 9. no secret in the repository -------------------------"
cd "$here/.."
git grep -nEi 'client_?secret|ClientSecret|AccountKey=|SharedAccessSignature|BEGIN [A-Z ]*PRIVATE KEY' -- . ':!*.md' || echo "no matches"
echo "tracked files under .azure:"
git ls-files .azure
echo "(empty above means the azd environment is untracked)"
echo "deployed credentials file tracked?"
git ls-files day17_swa_deploy/.deployed-credentials.json
echo "(empty above means it is gitignored)"

echo
echo "=============================================================="
echo " written to $here/verification.log"
echo "=============================================================="
