param(
    $SqlPort = 14333,
    $Database = 'ClockInDemo'
)

$ErrorActionPreference = 'Stop'
Get-ChildItem Env: | Where-Object {
    $_.Name -match '^(ConnectionStrings(:|__)ClockIn|[A-Z]*CONNSTR_ClockIn)$'
} | ForEach-Object { Remove-Item -LiteralPath "Env:$($_.Name)" }

# Validate inside the script so invalid values also clear the previous connection.
$parsedSqlPort = 0
if (-not [int]::TryParse([string]$SqlPort, [ref]$parsedSqlPort) -or $parsedSqlPort -lt 1 -or $parsedSqlPort -gt 65535) {
    throw 'Specify a SQL port between 1 and 65535.'
}
$SqlPort = $parsedSqlPort
$Database = [string]$Database
if ([string]::IsNullOrWhiteSpace($Database) -or $Database.Contains([string][char]0) -or
    $Database.Length -gt 128 -or $Database.Trim() -in @('master', 'tempdb', 'model', 'msdb')) {
    throw 'Specify a nonblank database name of at most 128 characters without NUL. Do not use master, tempdb, model, or msdb.'
}

$envFile = Join-Path $PSScriptRoot '.env'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
    throw 'Create .env and set the local SQL Server password before running this script.'
}

$docker = Get-Command docker -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $docker) {
    throw 'Docker CLI was not found. See Prerequisites in README.md.'
}
$compose = 'compose --project-directory . --env-file .env -f docker-compose.yml'

function Invoke-ClockInDocker {
    param(
        [string]$Arguments,
        [string]$FailureMessage,
        [ValidateRange(1, 2147483647)]
        [int]$TimeoutMilliseconds = 30000,
        [string]$SqlPassword
    )

    # Pin commands after context inspection to the endpoint we checked.
    if ($dockerEndpoint) {
        $Arguments = "--host `"$dockerEndpoint`" $Arguments"
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new($docker.Source, $Arguments)
    $startInfo.WorkingDirectory = $PSScriptRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.EnvironmentVariables.Remove('MSSQL_SA_PASSWORD')
    $startInfo.EnvironmentVariables.Remove('COMPOSE_PROJECT_NAME')
    if ($dockerEndpoint) {
        $startInfo.EnvironmentVariables.Remove('DOCKER_HOST')
        $startInfo.EnvironmentVariables.Remove('DOCKER_CONTEXT')
    }
    $startInfo.EnvironmentVariables['CLOCKIN_SQL_PORT'] = "$SqlPort"
    if ($PSBoundParameters.ContainsKey('SqlPassword')) {
        $startInfo.EnvironmentVariables['SQLCMDPASSWORD'] = $SqlPassword
    }

    $process = $null
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $process = [Diagnostics.Process]::Start($startInfo)
        $output = $process.StandardOutput.ReadToEndAsync()
        # Compose errors can include .env contents.
        $stderr = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        $remainingMilliseconds = [int][Math]::Max(0, $TimeoutMilliseconds - $stopwatch.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remainingMilliseconds) -or $process.ExitCode -ne 0) {
            throw $FailureMessage
        }
        # A Compose child can hold the redirected streams open after the CLI exits.
        $remainingMilliseconds = [int][Math]::Max(0, $TimeoutMilliseconds - $stopwatch.ElapsedMilliseconds)
        if (-not [Threading.Tasks.Task]::WaitAll(@($output, $stderr), $remainingMilliseconds)) {
            throw $FailureMessage
        }
        $output.GetAwaiter().GetResult()
    }
    catch {
        if ($process) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill()
                }
            }
            catch {
                # The process can exit during cleanup. Keep the command's sanitized error.
            }
        }
        throw $FailureMessage
    }
    finally {
        if ($process) {
            $process.Dispose()
        }
    }
}

$dockerEndpoint = $null
$dockerEndpoint = (Invoke-ClockInDocker 'context inspect --format "{{.Endpoints.docker.Host}}"' `
    'Docker could not inspect the active context. Select a local Docker context and retry.').Trim()
if ($dockerEndpoint -notmatch '\A(?:npipe:////\./pipe/[a-z0-9_.-]+|unix:///(?!/)[a-z0-9_./ -]+)\z') {
    throw 'ClockIn requires a local Docker context using a local named pipe or Unix socket. Select a local Docker context and check any DOCKER_HOST or DOCKER_CONTEXT override.'
}

$configJson = Invoke-ClockInDocker "$compose config --format json" `
    'Docker Compose could not read the local configuration. Check that MSSQL_SA_PASSWORD is set correctly in .env.'
try {
    $config = ConvertFrom-Json -InputObject $configJson
}
catch {
    throw 'Docker Compose returned invalid local configuration.'
}
# Compose escapes dollars in its rendered config so it can be parsed again.
$password = ([string]$config.services.sqlserver.environment.MSSQL_SA_PASSWORD).Replace('$$', '$')
if ([string]::IsNullOrWhiteSpace($password) -or $password -ceq 'ChangeThisLocalPassword!123') {
    throw 'Set MSSQL_SA_PASSWORD to your local SQL Server password in .env.'
}

$container = (Invoke-ClockInDocker "$compose ps --all --quiet sqlserver" `
    'Docker Compose could not check for an existing SQL Server container.').Trim()
if ($container) {
    if ($container -notmatch '\A[0-9a-f]{12,64}\z') {
        throw 'Docker Compose returned an unexpected SQL Server container ID.'
    }
    $bindingsJson = Invoke-ClockInDocker "inspect --format `"{{json .HostConfig.PortBindings}}`" $container" `
        'Docker could not check the existing SQL Server port binding.'
    $bindings = @(($bindingsJson | ConvertFrom-Json).'1433/tcp')
    if ($bindings.Count -ne 1 -or $bindings[0].HostIp -ne '127.0.0.1' -or $bindings[0].HostPort -ne "$SqlPort") {
        throw "The existing SQL Server container does not publish only on 127.0.0.1:${SqlPort}. See Advanced in README.md."
    }
}

$null = Invoke-ClockInDocker "$compose up --no-recreate --wait --wait-timeout 120 sqlserver" `
    -TimeoutMilliseconds 600000 `
    -FailureMessage 'SQL Server did not become healthy. If using an existing volume, check its configured password and see Advanced in README.md.'

$loginCommand = "$compose exec -T --interactive=false -e SQLCMDPASSWORD sqlserver /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1,1433 -U sa -d master -C -b -l 5 -t 5 -Q `"SELECT 1`""
$null = Invoke-ClockInDocker $loginCommand -SqlPassword $password -TimeoutMilliseconds 15000 `
    -FailureMessage 'Could not verify the sa login with MSSQL_SA_PASSWORD from .env. For an existing SQL Server volume, use its configured password. See Advanced in README.md.'

$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection['Server'] = "127.0.0.1,$SqlPort"
$connection['Database'] = $Database
$connection['User Id'] = 'sa'
$connection['Password'] = $password
$connection['TrustServerCertificate'] = 'True'
$connection['Encrypt'] = 'False'
Write-Host "ClockIn database target: 127.0.0.1:$SqlPort, database $Database."
$env:ConnectionStrings__ClockIn = $connection.ConnectionString
