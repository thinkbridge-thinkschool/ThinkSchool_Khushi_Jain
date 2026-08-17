<#
.SYNOPSIS
    Builds QuotesLab in a local SQL Server container and runs the Day-7 scripts,
    saving every result set under day7-joins-and-ctes/results.

.DESCRIPTION
    One command, reproducible output. The schema script drops and recreates the
    database and the seed uses fixed timestamps and no GETDATE(), so two runs on
    two machines produce identical files.

    The sa password is read from $env:MSSQL_SA_PASSWORD and is never passed as a
    command-line argument. sqlcmd picks it up from SQLCMDPASSWORD inside the
    container, sourced from the container's own environment, so it appears
    neither in the host process list nor in the captured output.

.PARAMETER SchemaOnly
    Rebuild the database and stop, without running the query scripts.

.PARAMETER Stop
    Stop and remove the container, and its data with it. The container holds no
    state worth keeping: the schema script rebuilds the database from scratch on
    every run.

.EXAMPLE
    $env:MSSQL_SA_PASSWORD = 'a-strong-generated-password'
    ./run-lab.ps1
#>
[CmdletBinding()]
param(
    [switch] $SchemaOnly,
    [switch] $Stop
)

$ErrorActionPreference = 'Stop'

$root        = $PSScriptRoot
$composeFile = Join-Path $root 'docker-compose.yml'
$resultsDir  = Join-Path $root 'day7-joins-and-ctes\results'
$container   = 'quoteslab-sql'

function Invoke-Compose {
    # Arguments are passed as an explicit array rather than as remaining
    # arguments, because PowerShell would otherwise try to bind '-d' and '-v'
    # as parameters of this function and fail.
    param([Parameter(Mandatory)] [string[]] $Arguments)

    & docker compose -f $composeFile @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-InContainer {
    param([Parameter(Mandatory)] [string] $ShellCommand)

    & docker compose -f $composeFile exec -T sql /bin/sh -c $ShellCommand
}

if ($Stop) {
    Invoke-Compose -Arguments @('down', '-v')
    Write-Host 'Container stopped and removed.'
    return
}

if ([string]::IsNullOrWhiteSpace($env:MSSQL_SA_PASSWORD)) {
    throw @'
MSSQL_SA_PASSWORD is not set.

SQL Server rejects weak passwords, so generate one rather than inventing one:

    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $env:MSSQL_SA_PASSWORD = [Convert]::ToBase64String($bytes) + '!aA1'

It lives in this shell session only. Do not add it to a file in this repository.
'@
}

Write-Host 'Starting SQL Server...'
Invoke-Compose -Arguments @('up', '-d')

# `up -d` returns as soon as the container is created, not when SQL Server is
# accepting connections. Poll the healthcheck rather than sleeping a guessed
# number of seconds; a cold first start is much slower than a warm restart.
$deadline = (Get-Date).AddMinutes(3)
while ($true) {
    $state = & docker inspect -f '{{.State.Health.Status}}' $container
    if ($LASTEXITCODE -ne 0) { $state = 'unknown' }
    $state = ($state | Select-Object -First 1)

    if ($state -eq 'healthy') { break }

    if ($state -eq 'unhealthy') {
        throw "Container $container reported unhealthy. Inspect it with: docker logs $container"
    }

    if ((Get-Date) -gt $deadline) {
        throw "SQL Server did not become healthy within three minutes (last status: '$state')."
    }

    Write-Host "  waiting for SQL Server (status: $state)..."
    Start-Sleep -Seconds 5
}
Write-Host 'SQL Server is healthy.'

if (-not (Test-Path $resultsDir)) {
    New-Item -ItemType Directory -Path $resultsDir | Out-Null
}

# Resolve sqlcmd once. The 2022 image ships mssql-tools18; older tags ship
# mssql-tools. tools18 validates the self-signed certificate the container
# generates for itself, which is why every call below passes -C to trust it.
$probe = Invoke-InContainer -ShellCommand @'
if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then echo /opt/mssql-tools18/bin/sqlcmd
elif [ -x /opt/mssql-tools/bin/sqlcmd ]; then echo /opt/mssql-tools/bin/sqlcmd
fi
'@

$sqlcmd = $probe | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($sqlcmd)) {
    throw 'No sqlcmd binary found inside the container.'
}
$sqlcmd = $sqlcmd.Trim()

function Invoke-SqlScript {
    param(
        [Parameter(Mandatory)] [string] $ContainerPath,
        [Parameter(Mandatory)] [string] $ResultFileName
    )

    $outputPath = Join-Path $resultsDir $ResultFileName
    Write-Host ''
    Write-Host "Running $ContainerPath -> $ResultFileName"

    # -b   stop on error, so a broken script fails the run instead of scrolling past
    # -I   QUOTED_IDENTIFIER on, which the filtered index needs to be usable
    # -W   trim column padding, so the captured files stay readable. sqlcmd
    #      rejects -y alongside it, which is fine: trimming makes the declared
    #      nvarchar width irrelevant to the output anyway
    $command = "SQLCMDPASSWORD=`"`$MSSQL_SA_PASSWORD`" $sqlcmd -S localhost -U sa -C -b -I -l 60 -W -s '|' -i $ContainerPath"

    $output = Invoke-InContainer -ShellCommand $command
    $exit   = $LASTEXITCODE

    $output | Out-File -FilePath $outputPath -Encoding utf8
    $output | Write-Host

    if ($exit -ne 0) {
        throw "$ContainerPath failed with exit code $exit. Output saved to $outputPath."
    }
}

Invoke-SqlScript -ContainerPath '/sql/schema/01_schema.sql' -ResultFileName '01_schema.txt'
Invoke-SqlScript -ContainerPath '/sql/schema/02_seed.sql'   -ResultFileName '02_seed.txt'

if ($SchemaOnly) {
    Write-Host ''
    Write-Host 'Schema and seed applied. Skipping query scripts (-SchemaOnly).'
    return
}

Invoke-SqlScript -ContainerPath '/sql/day7-joins-and-ctes/03_joins.sql'                -ResultFileName '03_joins.txt'
Invoke-SqlScript -ContainerPath '/sql/day7-joins-and-ctes/04_author_quote_summary.sql' -ResultFileName '04_author_quote_summary.txt'
Invoke-SqlScript -ContainerPath '/sql/day7-joins-and-ctes/05_recursive_cte.sql'        -ResultFileName '05_recursive_cte.txt'
Invoke-SqlScript -ContainerPath '/sql/day7-joins-and-ctes/06_plans.sql'                -ResultFileName '06_plans.txt'

Write-Host ''
Write-Host "Done. Result sets written to $resultsDir"
Write-Host 'Remove the container with: ./run-lab.ps1 -Stop'
