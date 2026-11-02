<#
.SYNOPSIS
    Deploys the API to an IIS site: migrate, swap the files, check health, roll back on failure.

.DESCRIPTION
    Runbook: docs/deployment/iis.md; decision: docs/adr/0033-delivery-pipeline.md.

      1. Preflight: IIS administration module, ASP.NET Core Hosting Bundle, app pool with No Managed Code,
         TemplateName.Api.dll in the publish folder.
      2. Migrate:   `dotnet TemplateName.Api.dll migrate` from the publish folder while the old version still serves.
                    A failure stops here with the site untouched.
      3. Swap:      copy the site to {SitePath}.previous (replacing the one before), write app_offline.htm, mirror the
                    publish folder over the site (appsettings.Production.json, logs and app_offline.htm stay), remove
                    app_offline.htm.
      4. Check:     poll HealthUrl for a bounded time.
      5. Roll back: if the swap or the check fails, restore {SitePath}.previous and fail. The database is not rolled
                    back: the migrations are expand-only, so the previous code runs on the new schema (ADR 0031).

    The connection string is read from the environment variable ConnectionStrings__Database and is never a
    parameter and never printed. {SitePath}.previous is always kept after a run, so a manual rollback stays possible.
    Trailing separators are dropped from both paths first, so {SitePath}.previous is a sibling of the site, never inside it.
    The script exits 0 on success and 1 on failure, whatever robocopy left in $LASTEXITCODE.

    Runs on Windows PowerShell 5.1 and PowerShell 7. Every parameter is optional here and checked in
    Invoke-Deployment, so dot-sourcing the file (the Pester tests do) neither prompts nor runs anything.

.PARAMETER PublishPath
    The folder `dotnet publish` wrote.

.PARAMETER SitePath
    The IIS site's physical path.

.PARAMETER AppPool
    The site's application pool; it must be set to No Managed Code.

.PARAMETER HealthUrl
    The readiness endpoint (/health/ready) that must answer 2xx after the swap.
#>
[CmdletBinding()]
param(
    [string] $PublishPath,
    [string] $SitePath,
    [string] $AppPool,
    [string] $HealthUrl
)

function Get-ServerOwnedExclusion {
    # robocopy switches for the files and folders the server owns: the swap neither overwrites, deletes nor backs them up.
    return @('/XF', 'appsettings.Production.json', 'app_offline.htm', '/XD', 'logs')
}

function Write-DeployLog {
    param([string] $Message)
    Write-Information -MessageData "deploy-iis: $Message" -InformationAction Continue
}

function Get-NormalizedPath {
    # Drops trailing separators (C:\Sites\App\ becomes C:\Sites\App). A root keeps its separator: trimmed, C:\ would become the
    # drive-relative C: and \ or / nothing at all.
    param([string] $Path)
    $trimmed = $Path.TrimEnd('\', '/')
    if (-not $trimmed -or $trimmed.EndsWith(':')) { return $Path }
    return $trimmed
}

function Get-PreviousPath {
    # The backup folder, next to the site: C:\Sites\App.previous, never C:\Sites\App\.previous, which /MIR would delete.
    param([string] $SitePath)
    return "$(Get-NormalizedPath -Path $SitePath).previous"
}

function Enable-Tls12 {
    # Windows PowerShell 5.1 runs on .NET Framework, whose default protocols may leave out TLS 1.2 (unless the machine opts in),
    # so a request to an https health URL fails before any answer. This adds TLS 1.2 for this process. A value of 0
    # (SystemDefault) already lets Windows choose, TLS 1.2 included, and is left alone; PowerShell 7 always uses the OS defaults.
    param([string] $Edition = $PSVersionTable.PSEdition)
    if ($Edition -ne 'Desktop') { return }
    $current = [Net.ServicePointManager]::SecurityProtocol
    if ([int]$current -eq 0) { return }
    [Net.ServicePointManager]::SecurityProtocol = $current -bor [Net.SecurityProtocolType]::Tls12
}

# --- Thin wrappers around the machine: the tests mock these -------------------------------------------------------------

function Invoke-Robocopy {
    # Returns robocopy's exit code. Each element of ArgumentList is one argument, so PowerShell quotes a path that
    # holds a space; callers must not add quotes themselves.
    param([string[]] $ArgumentList)
    & robocopy @ArgumentList | Out-Host
    return $LASTEXITCODE
}

function Invoke-Dotnet {
    # Returns dotnet's exit code, with the process running in WorkingDirectory.
    param([string] $WorkingDirectory, [string[]] $ArgumentList)
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & dotnet @ArgumentList | Out-Host
        return $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}

function Test-HealthEndpoint {
    param([string] $Url)
    try {
        Enable-Tls12
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300)
    }
    catch {
        return $false
    }
}

function Test-IisModuleAvailable {
    return [bool](Get-Module -ListAvailable -Name IISAdministration)
}

function Test-HostingBundleInstalled {
    # The Hosting Bundle installs the ASP.NET Core Module for IIS.
    if (-not $env:ProgramFiles) { return $false }
    $module = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    return (Test-Path -LiteralPath $module)
}

function Get-AppPoolInfo {
    # $null when the pool does not exist; an empty ManagedRuntimeVersion means No Managed Code.
    param([string] $AppPool)
    Import-Module IISAdministration -ErrorAction Stop
    $pool = Get-IISAppPool -Name $AppPool -ErrorAction SilentlyContinue
    if (-not $pool) { return $null }
    return [pscustomobject]@{
        Name                  = $AppPool
        ManagedRuntimeVersion = [string]$pool.ManagedRuntimeVersion
    }
}

# --- Steps --------------------------------------------------------------------------------------------------------------

function Test-IisPreflight {
    param([string] $PublishPath, [string] $AppPool)

    if (-not (Test-IisModuleAvailable)) {
        throw 'The IIS administration module (IISAdministration) is not installed. Add the IIS management scripts and tools.'
    }
    if (-not (Test-HostingBundleInstalled)) {
        throw 'The ASP.NET Core Hosting Bundle is not installed (the ASP.NET Core Module for IIS is missing).'
    }
    $pool = Get-AppPoolInfo -AppPool $AppPool
    if (-not $pool) {
        throw "The app pool '$AppPool' does not exist."
    }
    if ($pool.ManagedRuntimeVersion) {
        throw "The app pool '$AppPool' must be set to No Managed Code; its .NET CLR version is '$($pool.ManagedRuntimeVersion)'."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $PublishPath 'TemplateName.Api.dll'))) {
        throw "TemplateName.Api.dll is not in the publish path '$PublishPath'."
    }
    Write-DeployLog "Preflight passed (app pool '$AppPool', publish path '$PublishPath')."
}

function Invoke-Migration {
    param([string] $PublishPath)

    # dotnet reads the connection string from this variable itself; the script only checks that it is there and never
    # prints it, and neither does the migrate mode.
    if (-not [Environment]::GetEnvironmentVariable('ConnectionStrings__Database')) {
        throw 'The environment variable ConnectionStrings__Database is not set; the migration needs it.'
    }
    Write-DeployLog 'Running the database migration.'
    $exitCode = Invoke-Dotnet -WorkingDirectory $PublishPath -ArgumentList @('TemplateName.Api.dll', 'migrate')
    if ($exitCode -ne 0) {
        throw "The database migration failed with exit code $exitCode."
    }
    Write-DeployLog 'The database migration succeeded.'
}

function Backup-Site {
    param([string] $SitePath)

    $SitePath = Get-NormalizedPath -Path $SitePath
    $previous = Get-PreviousPath -SitePath $SitePath
    # Replace, never merge: a file removed since the last release must not linger in the backup.
    if (Test-Path -LiteralPath $previous) {
        Remove-Item -LiteralPath $previous -Recurse -Force -ErrorAction Stop
    }
    if (-not (Test-Path -LiteralPath $SitePath)) {
        Write-DeployLog "There is no site at '$SitePath' yet; nothing to back up."
        return
    }
    Write-DeployLog "Backing up '$SitePath' to '$previous'."
    $arguments = @($SitePath.TrimEnd('\', '/'), $previous.TrimEnd('\', '/'), '/E', '/R:3', '/W:5', '/NFL', '/NDL', '/NP') + (Get-ServerOwnedExclusion)
    $exitCode = Invoke-Robocopy -ArgumentList $arguments
    if ($exitCode -ge 8) {
        throw "robocopy failed with exit code $exitCode while backing up the site."
    }
}

function Publish-Site {
    param([string] $PublishPath, [string] $SitePath)

    Write-DeployLog "Mirroring '$PublishPath' over '$SitePath'."
    # /MIR deletes what the publish folder lacks, except what /XF and /XD exclude (those stay on both sides).
    $arguments = @($PublishPath.TrimEnd('\', '/'), $SitePath.TrimEnd('\', '/'), '/MIR', '/R:3', '/W:5', '/NFL', '/NDL', '/NP') + (Get-ServerOwnedExclusion)
    $exitCode = Invoke-Robocopy -ArgumentList $arguments
    # robocopy reports success with 0 to 7 (bit flags for what it copied); 8 and above mean a failure.
    if ($exitCode -ge 8) {
        throw "robocopy failed with exit code $exitCode while publishing the site."
    }
}

function Set-SiteOffline {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'A pipeline step that must always run; -WhatIf would silently skip the offline page the swap relies on.')]
    [CmdletBinding()]
    param([string] $SitePath)

    if (-not (Test-Path -LiteralPath $SitePath)) {
        New-Item -ItemType Directory -Path $SitePath -Force -ErrorAction Stop | Out-Null
    }
    $page = '<!DOCTYPE html><html><head><meta charset="utf-8"><title>Back soon</title></head>' +
        '<body><h1>Down for maintenance</h1><p>The service is being updated and will be back in a moment.</p></body></html>'
    Set-Content -LiteralPath (Join-Path $SitePath 'app_offline.htm') -Value $page -Encoding ASCII -ErrorAction Stop
    Write-DeployLog 'The site is offline (app_offline.htm written).'
}

function Set-SiteOnline {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'A pipeline step that must always run; -WhatIf would silently leave the site offline.')]
    [CmdletBinding()]
    param([string] $SitePath)

    $page = Join-Path $SitePath 'app_offline.htm'
    if (Test-Path -LiteralPath $page) {
        Remove-Item -LiteralPath $page -Force -ErrorAction Stop
    }
    Write-DeployLog 'The site is online (app_offline.htm removed).'
}

function Wait-Healthy {
    param([string] $HealthUrl, [int] $TimeoutSeconds = 120)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        if (Test-HealthEndpoint -Url $HealthUrl) {
            Write-DeployLog 'The health check passed.'
            return $true
        }
        if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
        Start-Sleep -Seconds 3
    }
    Write-DeployLog "The health check did not pass within $TimeoutSeconds seconds."
    return $false
}

function Restore-Site {
    param([string] $SitePath)

    $SitePath = Get-NormalizedPath -Path $SitePath
    $previous = Get-PreviousPath -SitePath $SitePath
    if (-not (Test-Path -LiteralPath $previous)) {
        # A first deployment: nothing to restore, so the site stays as the failed step left it.
        throw "There is no previous version at '$previous' to restore."
    }
    Write-DeployLog "Restoring '$previous' over '$SitePath'."
    # Offline while the files change, as for the swap. If the restore itself fails the site stays offline, on purpose:
    # a maintenance page is better than a half restored site answering requests.
    Set-SiteOffline -SitePath $SitePath
    $arguments = @($previous.TrimEnd('\', '/'), $SitePath.TrimEnd('\', '/'), '/MIR', '/R:3', '/W:5', '/NFL', '/NDL', '/NP') + (Get-ServerOwnedExclusion)
    $exitCode = Invoke-Robocopy -ArgumentList $arguments
    if ($exitCode -ge 8) {
        throw "robocopy failed with exit code $exitCode while restoring the previous version; the site was left offline."
    }
    Set-SiteOnline -SitePath $SitePath
}

function Invoke-Deployment {
    param([string] $PublishPath, [string] $SitePath, [string] $AppPool, [string] $HealthUrl)

    $ErrorActionPreference = 'Stop'

    $missing = @()
    if (-not $PublishPath) { $missing += 'PublishPath' }
    if (-not $SitePath) { $missing += 'SitePath' }
    if (-not $AppPool) { $missing += 'AppPool' }
    if (-not $HealthUrl) { $missing += 'HealthUrl' }
    if ($missing.Count -gt 0) {
        throw "Missing parameter(s): $($missing -join ', '). Usage: deploy-iis.ps1 -PublishPath <dir> -SitePath <dir> -AppPool <name> -HealthUrl <url>"
    }

    # Once, before any step: with a trailing separator {SitePath}.previous would land inside the site (IIS_SITE_PATH=C:\inetpub\api\).
    $PublishPath = Get-NormalizedPath -Path $PublishPath
    $SitePath = Get-NormalizedPath -Path $SitePath
    if ($SitePath -ne $SitePath.TrimEnd('\', '/') -or $SitePath.EndsWith(':')) {
        # /MIR over a root would delete everything on it that the publish folder lacks.
        throw "The site path '$SitePath' is a root; the site must be a folder of its own."
    }

    Test-IisPreflight -PublishPath $PublishPath -AppPool $AppPool
    # While the old version still serves; a failure here ends the run with the site untouched (design D8).
    Invoke-Migration -PublishPath $PublishPath
    Backup-Site -SitePath $SitePath

    try {
        Set-SiteOffline -SitePath $SitePath
        Publish-Site -PublishPath $PublishPath -SitePath $SitePath
        Set-SiteOnline -SitePath $SitePath
        if (-not (Wait-Healthy -HealthUrl $HealthUrl -TimeoutSeconds 120)) {
            throw "The health check at $HealthUrl did not pass."
        }
    }
    catch {
        $reason = $_.Exception.Message
        Write-DeployLog "The deployment failed: $reason Restoring the previous files."
        try {
            Restore-Site -SitePath $SitePath
        }
        catch {
            throw "The deployment failed ($reason) and the previous files could not be restored: $($_.Exception.Message)"
        }
        # The files only: the restored version is not health-checked here.
        throw "The deployment failed and the previous files were restored: $reason"
    }
    Write-DeployLog 'The deployment succeeded.'
    # robocopy leaves 1 or 3 in $LASTEXITCODE after a good copy; a caller that ends with `exit $LASTEXITCODE` (the Actions runner
    # does for a powershell step) must see 0.
    $global:LASTEXITCODE = 0
}

# Dot-sourcing (the tests) only defines the functions; running the file deploys. The explicit exit codes matter: the Actions
# runner appends `if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }` to a powershell step.
if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-Deployment -PublishPath $PublishPath -SitePath $SitePath -AppPool $AppPool -HealthUrl $HealthUrl
    }
    catch {
        [Console]::Error.WriteLine("deploy-iis: $($_.Exception.Message)")
        exit 1
    }
    exit 0
}
