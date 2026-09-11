#!/usr/bin/env bash
set -euo pipefail

# CREATE USER FROM EXTERNAL PROVIDER runs inside the database as the Entra administrator, so no template can do it.

RESOURCE_GROUP="${1:-rg-docbook}"

value() {
    az deployment group show --resource-group "$RESOURCE_GROUP" --name main \
        --query "properties.outputs.$1.value" --output tsv
}

server=$(value sqlServerFullyQualifiedDomainName)
database=$(value sqlDatabaseName)
app=$(value apiName)

if [[ -z "$server" || -z "$database" || -z "$app" ]]; then
    echo "Deploy first -- these three names are outputs of the deployment." >&2
    exit 1
fi

# db_ddladmin because the app applies its own migrations at startup.
grant=$(cat <<SQL
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$app')
    CREATE USER [$app] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [$app];
ALTER ROLE db_datawriter ADD MEMBER [$app];
ALTER ROLE db_ddladmin ADD MEMBER [$app];
SQL
)

if command -v sqlcmd > /dev/null && sqlcmd -S "$server" -d "$database" -G -Q "$grant"; then
    echo "Granted $app on $database." >&2
    exit 0
fi

# The Entra flags differ between the ODBC and Go builds of sqlcmd, so the fallback is the portal's Query editor.
echo "Could not run it from here. Paste this into the Query editor for $database on $server:" >&2
printf '\n%s\n' "$grant"
