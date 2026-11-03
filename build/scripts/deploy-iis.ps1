<#
.SYNOPSIS
    Deploys the API to an IIS site: migrate, swap the files, check health, roll back on failure.

.DESCRIPTION
    Runbook: docs/deployment/iis.md; decision: docs/adr/0033-delivery-pipeline.md.

      1. Preflight: IIS administration module, ASP.NET Core Hosting Bundle, app pool with No Managed Code,
         TemplateName.Api.dll in the publish folder, the maintenance page.
      2. Migrate:   `dotnet TemplateName.Api.dll migrate` from the publish folder while the old version still serves.
                    A failure stops here with the site untouched.
      3. Swap:      copy the site to {SitePath}.previous (replacing the one before), copy the maintenance page to
                    app_offline.htm (byte for byte), mirror the publish folder over the site (appsettings.Production.json,
                    logs and app_offline.htm stay), remove app_offline.htm.
      4. Check:     poll HealthUrl for a bounded time.
      5. Roll back: if the swap or the check fails, restore {SitePath}.previous and fail. The database is not rolled
                    back: the migrations are expand-only, so the previous code runs on the new schema (ADR 0031).

    With -Maintenance (a release that contains a contract migration, see docs/api-compatibility.md) the order is:
    preflight, back up the site to {SitePath}.previous, put the maintenance page up (app_offline.htm), migrate, swap the
    files, remove the page, check. Nothing is rolled back in this mode: the contract migration may already have changed the
    schema, so restoring the old files could not work. A failure after the page is up leaves the page up and the files as
    they are, and the script fails; the message says to restore the database from a backup first, then restore
    {SitePath}.previous and remove app_offline.htm by hand. A failure before the page is up (preflight, backup) leaves the
    site untouched.

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

.PARAMETER MaintenancePage
    The page IIS serves while the site is offline; it is copied to app_offline.htm. Defaults to build/deploy/maintenance.html
    next to this script's folder (the page the Docker rollout shares).

.PARAMETER Maintenance
    Runs the maintenance mode described above instead of the normal order.
#>
[CmdletBinding()]
param(
    [string] $PublishPath,
    [string] $SitePath,
    [string] $AppPool,
    [string] $HealthUrl,
    [string] $MaintenancePage,
    [switch] $Maintenance
)

function Get-ServerOwnedExclusion {
    # robocopy switches for the files and folders the server owns: the swap neither overwrites, deletes nor backs them up.
    # /XF and /XD match a bare name at any depth: a nested wwwroot\logs, or an appsettings.Production.json in a subfolder, is skipped too.
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
    # The Hosting Bundle installs the ASP.NET Core Module for IIS. ProgramW6432 is the 64-bit Program Files even for a 32-bit
    # host process, whose ProgramFiles is "Program Files (x86)", where the module is not.
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    if (-not $programFiles) { return $false }
    $module = Join-Path $programFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
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
    param([string] $PublishPath, [string] $AppPool, [string] $MaintenancePage)

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
    # Last, and only when a page is given: the page is what a deployment puts up, so it must be there before any change.
    if ($MaintenancePage -and -not (Test-Path -LiteralPath $MaintenancePage -PathType Leaf)) {
        throw "The maintenance page '$MaintenancePage' does not exist."
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
    param([string] $SitePath, [string] $MaintenancePage)

    if (-not (Test-Path -LiteralPath $SitePath)) {
        New-Item -ItemType Directory -Path $SitePath -Force -ErrorAction Stop | Out-Null
    }
    # A file copy, never Get-Content/Set-Content: the page is UTF-8 with text outside ASCII, which a re-encoding would damage.
    Copy-Item -LiteralPath $MaintenancePage -Destination (Join-Path $SitePath 'app_offline.htm') -Force -ErrorAction Stop
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
    param([string] $SitePath, [string] $MaintenancePage)

    $SitePath = Get-NormalizedPath -Path $SitePath
    $previous = Get-PreviousPath -SitePath $SitePath
    if (-not (Test-Path -LiteralPath $previous)) {
        # A first deployment: nothing to restore, so the site stays as the failed step left it.
        throw "There is no previous version at '$previous' to restore."
    }
    Write-DeployLog "Restoring '$previous' over '$SitePath'."
    # Offline while the files change, as for the swap. If the restore itself fails the site stays offline, on purpose:
    # a maintenance page is better than a half restored site answering requests.
    Set-SiteOffline -SitePath $SitePath -MaintenancePage $MaintenancePage
    $arguments = @($previous.TrimEnd('\', '/'), $SitePath.TrimEnd('\', '/'), '/MIR', '/R:3', '/W:5', '/NFL', '/NDL', '/NP') + (Get-ServerOwnedExclusion)
    $exitCode = Invoke-Robocopy -ArgumentList $arguments
    if ($exitCode -ge 8) {
        throw "robocopy failed with exit code $exitCode while restoring the previous version; the site was left offline."
    }
    Set-SiteOnline -SitePath $SitePath
}

function Invoke-Deployment {
    param([string] $PublishPath, [string] $SitePath, [string] $AppPool, [string] $HealthUrl, [string] $MaintenancePage, [switch] $Maintenance)

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
    # A drive root (C:\) or a UNC share root (\\server\share, trailing separators already dropped, or \\server alone).
    $isRoot = $SitePath -ne $SitePath.TrimEnd('\', '/') -or $SitePath.EndsWith(':') -or $SitePath -match '^[\\/]{2}[^\\/]+([\\/][^\\/]+)?$'
    if ($isRoot) {
        # /MIR over a root would delete everything on it that the publish folder lacks.
        throw "The site path '$SitePath' is a root (a drive or a share); the site must be a folder of its own."
    }

    if (-not $MaintenancePage) {
        $MaintenancePage = Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy/maintenance.html'
    }

    Test-IisPreflight -PublishPath $PublishPath -AppPool $AppPool -MaintenancePage $MaintenancePage

    if ($Maintenance) {
        # Maintenance mode: the page goes up before the migration, because a contract migration changes the schema under the
        # old code. A failure before the page is up (preflight, backup) leaves the site untouched.
        Backup-Site -SitePath $SitePath
        Set-SiteOffline -SitePath $SitePath -MaintenancePage $MaintenancePage
        $step = 'migrate'
        try {
            Invoke-Migration -PublishPath $PublishPath
            $step = 'swap'
            Publish-Site -PublishPath $PublishPath -SitePath $SitePath
            $step = 'health check'
            Set-SiteOnline -SitePath $SitePath
            if (-not (Wait-Healthy -HealthUrl $HealthUrl -TimeoutSeconds 120)) {
                throw "The health check at $HealthUrl did not pass."
            }
        }
        catch {
            $reason = $_.Exception.Message
            $previous = Get-PreviousPath -SitePath $SitePath
            Write-DeployLog "The deployment failed at $step in maintenance mode: $reason The maintenance page stays up; nothing is restored."
            if ($step -eq 'health check') {
                # The page came down for the check; put it back, best effort, so users never reach a version that is not healthy.
                try {
                    Set-SiteOffline -SitePath $SitePath -MaintenancePage $MaintenancePage
                }
                catch {
                    Write-DeployLog "The maintenance page could not be put back: $($_.Exception.Message)"
                }
            }
            throw "The deployment failed at $step in maintenance mode: $reason The maintenance page is still up. If a contract migration was applied, restore the database from a backup first; then restore '$previous' over '$SitePath' and remove app_offline.htm."
        }
        Write-DeployLog 'The deployment succeeded.'
        $global:LASTEXITCODE = 0
        return
    }

    # While the old version still serves; a failure here ends the run with the site untouched (design D8).
    Invoke-Migration -PublishPath $PublishPath
    Backup-Site -SitePath $SitePath

    try {
        Set-SiteOffline -SitePath $SitePath -MaintenancePage $MaintenancePage
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
            Restore-Site -SitePath $SitePath -MaintenancePage $MaintenancePage
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
        Invoke-Deployment -PublishPath $PublishPath -SitePath $SitePath -AppPool $AppPool -HealthUrl $HealthUrl -MaintenancePage $MaintenancePage -Maintenance:$Maintenance
    }
    catch {
        [Console]::Error.WriteLine("deploy-iis: $($_.Exception.Message)")
        exit 1
    }
    exit 0
}
