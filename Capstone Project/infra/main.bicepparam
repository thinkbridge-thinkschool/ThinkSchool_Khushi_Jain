using './main.bicep'

param applicationName = 'docbook'

// Read from the shell, so the administrator's object id is not checked in.
param administratorPrincipalId = readEnvironmentVariable('DOCBOOK_ADMIN_OBJECT_ID')

// Enabled for the first deploy only, while the database grant and the signing key are set.
param publicNetworkAccess = readEnvironmentVariable('DOCBOOK_PUBLIC_ACCESS', 'Disabled')
