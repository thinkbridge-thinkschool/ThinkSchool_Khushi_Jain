<#
.SYNOPSIS
    Builds QuotesLab in a local SQL Server container and runs the Day-7, Day-8
    and Day-9 scripts, saving every result set under the matching exercise
    folder.

.DESCRIPTION
    One command, reproducible output. The schema script drops and recreates the
    database and the seed uses fixed timestamps and no GETDATE(), so two runs on
    two machines produce identical files. Day 8's 100,000-row table is generated
    arithmetically from row numbers for the same reason. Day 9 needs two
    connections open at once, so its two scripts run concurrently and coordinate
    through a signal table rather than through timing.

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

# sqlcmd runs inside a Linux container and emits UTF-8. PowerShell decodes a
# native command's output using [Console]::OutputEncoding, which defaults to the
# OEM codepage in some hosts -- notably when this script is launched as
# `powershell -File` from Git Bash rather than from a PowerShell session. Every
# non-ASCII character then round-trips into mojibake before Out-File writes it,
# so an em dash in a PRINT banner lands in the captured evidence as three
# characters of noise. Pinning the encoding makes the captured files identical
# whichever shell started the run, which is the whole promise of results/.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root        = $PSScriptRoot
$composeFile = Join-Path $root 'docker-compose.yml'
$container   = 'quoteslab-sql'

# One results directory per exercise piece. The schema and seed output lands
# with the joins piece rather than in a directory of its own, because that is
# where it was first captured and the folder has already been handed to a
# mentor as a link.
$joinsResults     = Join-Path $root 'day7-joins-and-ctes\results'
$windowResults    = Join-Path $root 'day7-window-functions\results'
$setResults       = Join-Path $root 'day7-set-operations\results'
$indexResults     = Join-Path $root 'day8-indexes\results'
$coverResults     = Join-Path $root 'day8-covering-indexes\results'
$isolationResults = Join-Path $root 'day9-isolation-levels\results'

function Invoke-Native {
    # docker writes progress and diagnostics to stderr as a matter of course.
    # If the caller has merged the streams -- ./run-lab.ps1 2>&1 > log.txt, or
    # most CI log capture -- PowerShell 5.1 wraps each of those lines in an
    # ErrorRecord, and $ErrorActionPreference = 'Stop' then treats ordinary
    # progress output as a fatal error. Every caller below checks $LASTEXITCODE
    # explicitly, so the preference was never doing the work here anyway.
    param([Parameter(Mandatory)] [scriptblock] $Command)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try     { & $Command }
    finally { $ErrorActionPreference = $previous }
}

function Invoke-Compose {
    # Arguments are passed as an explicit array rather than as remaining
    # arguments, because PowerShell would otherwise try to bind '-d' and '-v'
    # as parameters of this function and fail.
    param([Parameter(Mandatory)] [string[]] $Arguments)

    Invoke-Native { & docker compose -f $composeFile @Arguments }
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-InContainer {
    param([Parameter(Mandatory)] [string] $ShellCommand)

    Invoke-Native { & docker compose -f $composeFile exec -T sql /bin/sh -c $ShellCommand }
}

if ($Stop) {
    # Tearing down needs no credential, but Compose still refuses to parse a
    # file whose ${MSSQL_SA_PASSWORD:?...} guard is unsatisfied. A placeholder
    # is enough to get the file parsed; `down` never connects to SQL Server, so
    # the value is genuinely unused. Without this, stopping the lab from a fresh
    # shell fails with an interpolation error that has nothing to do with the
    # actual problem.
    if ([string]::IsNullOrWhiteSpace($env:MSSQL_SA_PASSWORD)) {
        $env:MSSQL_SA_PASSWORD = 'unused-for-teardown'
    }

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
    $state = Invoke-Native { & docker inspect -f '{{.State.Health.Status}}' $container }
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

foreach ($dir in @($joinsResults, $windowResults, $setResults, $indexResults, $coverResults, $isolationResults)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
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

# -b   stop on error, so a broken script fails the run instead of scrolling past
# -I   QUOTED_IDENTIFIER on, which the filtered index needs to be usable
# -W   trim column padding, so the captured files stay readable. sqlcmd rejects
#      -y alongside it, which is fine: trimming makes the declared nvarchar
#      width irrelevant to the output anyway
$sqlcmdInvocation = "SQLCMDPASSWORD=`"`$MSSQL_SA_PASSWORD`" $sqlcmd -S localhost -U sa -C -b -I -l 60 -W -s '|'"

function Invoke-SqlScript {
    param(
        [Parameter(Mandatory)] [string] $ContainerPath,
        [Parameter(Mandatory)] [string] $ResultDirectory,
        [Parameter(Mandatory)] [string] $ResultFileName
    )

    $outputPath = Join-Path $ResultDirectory $ResultFileName
    Write-Host ''
    Write-Host "Running $ContainerPath -> $ResultFileName"

    $output = Invoke-InContainer -ShellCommand "$sqlcmdInvocation -i $ContainerPath"
    $exit   = $LASTEXITCODE

    $output | Out-File -FilePath $outputPath -Encoding utf8
    $output | Write-Host

    if ($exit -ne 0) {
        throw "$ContainerPath failed with exit code $exit. Output saved to $outputPath."
    }
}

Invoke-SqlScript -ContainerPath '/sql/schema/01_schema.sql' -ResultDirectory $joinsResults -ResultFileName '01_schema.txt'
Invoke-SqlScript -ContainerPath '/sql/schema/02_seed.sql'   -ResultDirectory $joinsResults -ResultFileName '02_seed.txt'

if ($SchemaOnly) {
    Write-Host ''
    Write-Host 'Schema and seed applied. Skipping query scripts (-SchemaOnly).'
    return
}

Invoke-SqlScript -ContainerPath '/sql/day7-joins-and-ctes/03_joins.sql'                -ResultDirectory $joinsResults -ResultFileName '03_joins.txt'
Invoke-SqlScript -ContainerPath '/sql/day7-joins-and-ctes/04_author_quote_summary.sql' -ResultDirectory $joinsResults -ResultFileName '04_author_quote_summary.txt'
Invoke-SqlScript -ContainerPath '/sql/day7-joins-and-ctes/05_recursive_cte.sql'        -ResultDirectory $joinsResults -ResultFileName '05_recursive_cte.txt'
Invoke-SqlScript -ContainerPath '/sql/day7-joins-and-ctes/06_plans.sql'                -ResultDirectory $joinsResults -ResultFileName '06_plans.txt'

Invoke-SqlScript -ContainerPath '/sql/day7-window-functions/07_window_functions.sql'   -ResultDirectory $windowResults -ResultFileName '07_window_functions.txt'
Invoke-SqlScript -ContainerPath '/sql/day7-set-operations/08_set_operations.sql'       -ResultDirectory $setResults    -ResultFileName '08_set_operations.txt'

# Day 8 builds its own 100,000-row table, which adds a minute to the run. Piece 2
# reads the table piece 1 leaves behind, so it has to run second.
Invoke-SqlScript -ContainerPath '/sql/day8-indexes/index_lab.sql'                   -ResultDirectory $indexResults -ResultFileName 'index_lab.txt'
Invoke-SqlScript -ContainerPath '/sql/day8-covering-indexes/covering_index_lab.sql' -ResultDirectory $coverResults -ResultFileName 'covering_index_lab.txt'

# Day 9 is the one piece that cannot be a single sqlcmd run: an uncommitted write
# is only visible to a second connection while the first one is still open. Both
# sqlcmd processes are therefore started inside the container and waited on
# together, each writing to its own file so the two transcripts stay readable.
# Session A owns the lab objects, so B is started a moment later; B also polls
# for them, which is what actually makes the ordering safe.
Write-Host ''
Write-Host 'Running the Day-9 two-session lab -> session_a.txt, session_b.txt'

# One line, because PowerShell 5.1 does not pass a multi-line string to a native
# command intact. Session B runs in the foreground and A in the background, so
# the single `wait` is enough and no process ids have to survive the quoting.
$sessionA = "$sqlcmdInvocation -i /sql/day9-isolation-levels/session_a.sql > /tmp/session_a.txt 2>&1"
$sessionB = "$sqlcmdInvocation -i /sql/day9-isolation-levels/session_b.sql > /tmp/session_b.txt 2>&1"

Invoke-InContainer -ShellCommand "$sessionA & sleep 2; $sessionB; wait" | Write-Host

$day9Failures = @()

foreach ($session in @('session_a', 'session_b')) {
    $outputPath = Join-Path $isolationResults "$session.txt"
    $output     = Invoke-InContainer -ShellCommand "cat /tmp/$session.txt"

    $output | Out-File -FilePath $outputPath -Encoding utf8
    Write-Host ''
    Write-Host "--- $session ---"
    $output | Write-Host

    # sqlcmd's exit code is spent on the backgrounded half, so the captured
    # transcript is what gets checked. An empty file means sqlcmd never ran.
    if ([string]::IsNullOrWhiteSpace($output -join '')) {
        $day9Failures += "$session produced no output"
    }
    elseif ($output -match 'Msg \d+, Level \d+') {
        $day9Failures += "$session reported a SQL error"
    }
}

if ($day9Failures.Count -gt 0) {
    throw "The Day-9 two-session lab failed: $($day9Failures -join '; '). Output saved to $isolationResults."
}

Write-Host ''
Write-Host "Done. Result sets written to:"
Write-Host "  $joinsResults"
Write-Host "  $windowResults"
Write-Host "  $setResults"
Write-Host "  $indexResults"
Write-Host "  $coverResults"
Write-Host "  $isolationResults"
Write-Host 'Remove the container with: ./run-lab.ps1 -Stop'
