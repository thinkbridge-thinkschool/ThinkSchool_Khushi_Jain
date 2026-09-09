#!/usr/bin/env bash
set -euo pipefail

# CREATE USER FROM EXTERNAL PROVIDER runs inside the database as the Entra administrator, so no template can do it.

value() {
    azd env get-values | grep "^$1=" | cut -d'"' -f2 || true
}

server=$(value AZURE_SQL_SERVER)
database=$(value AZURE_SQL_DATABASE)
identity=$(value AZURE_QUOTES_API_IDENTITY_NAME)

if [[ -z "$server" || -z "$database" || -z "$identity" ]]; then
    echo "Provision first -- these three names are outputs of the deployment." >&2
    exit 1
fi

# db_ddladmin because the app applies its own migrations at startup.
grant=$(cat <<SQL
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$identity')
    CREATE USER [$identity] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [$identity];
ALTER ROLE db_datawriter ADD MEMBER [$identity];
ALTER ROLE db_ddladmin ADD MEMBER [$identity];
SQL
)

if command -v sqlcmd > /dev/null && sqlcmd -S "$server" -d "$database" -G -Q "$grant"; then
    echo "Granted $identity on $database." >&2
    exit 0
fi

# The Entra flags differ between the ODBC and Go builds of sqlcmd, so the fallback is the portal's Query editor.
echo "Could not run it from here. Paste this into the Query editor for $database on $server:" >&2
printf '\n%s\n' "$grant"
