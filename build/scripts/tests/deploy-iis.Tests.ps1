# Pester 5 tests for build/scripts/deploy-iis.ps1 (docs/deployment/iis.md; decision: docs/adr/0033-delivery-pipeline.md).
# They run under Windows PowerShell 5.1 and PowerShell 7 on any OS: every IIS, process and robocopy call sits in a small function
# of the script that is mocked here, the Windows-only cmdlet is stubbed, and the one test that runs a real robocopy is skipped
# off Windows.

BeforeDiscovery {
    $script:OnWindows = ($env:OS -eq 'Windows_NT')
}

BeforeAll {
    $script:ScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy-iis.ps1'

    # The Windows-only IIS cmdlet does not exist on Linux; a stub lets Pester mock it there.
    if (-not (Get-Command Get-IISAppPool -ErrorAction SilentlyContinue)) {
        function Get-IISAppPool { param([string] $Name) $null = $Name }
    }

    # Dot-sourcing defines the functions without running the deployment.
    . $script:ScriptPath

    # A single place to record the order of calls that every mock appends to.
    $script:Calls = [System.Collections.Generic.List[string]]::new()
    $script:Logged = [System.Collections.Generic.List[string]]::new()

    # $TestDrive lives for the whole Describe, so each test works in a folder of its own.
    function Initialize-WorkFolder {
        $folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder | Out-Null
        return $folder
    }

    function Add-ContentFile {
        param([string] $Path, [string] $Value = 'x')
        $directory = Split-Path -Parent $Path
        if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
        Set-Content -LiteralPath $Path -Value $Value
    }
}

Describe 'deploy-iis.ps1 as a script' {
    It 'has no ConnectionString parameter' {
        $names = (Get-Command $script:ScriptPath).Parameters.Keys
        $names | Should -Not -Contain 'ConnectionString'
        ($names | Where-Object { $_ -match 'Connection|Password|Secret' }) | Should -BeNullOrEmpty
        $names | Should -Contain 'PublishPath'
        $names | Should -Contain 'SitePath'
        $names | Should -Contain 'AppPool'
        $names | Should -Contain 'HealthUrl'
    }

    It 'keeps every parameter optional so that dot-sourcing never prompts' {
        $mandatory = (Get-Command $script:ScriptPath).Parameters.Values |
            Where-Object { $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } }
        $mandatory | Should -BeNullOrEmpty
    }

    It 'runs the deployment when it is executed, and exits 1 naming the missing parameters' {
        $shell = (Get-Process -Id $PID).Path
        $output = & $shell -NoProfile -NonInteractive -File $script:ScriptPath 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output | Should -Match 'PublishPath'
        $output | Should -Match 'HealthUrl'
    }

    # The Actions runner writes a `shell: powershell` (or pwsh) step to a file, prepends $ErrorActionPreference = 'stop', appends
    # the line below and runs `powershell -command ". '<file>'"`. robocopy leaves 1 or 3 in $LASTEXITCODE after a good /MIR, so a
    # script that returns without resetting it turns a successful deployment into a red step. The machine is stubbed in the child
    # process (an IISAdministration module, the Hosting Bundle's file, and global dotnet, robocopy and Invoke-WebRequest functions,
    # which a script finds before the real commands), so the real script runs end to end on any OS.
    It 'ends the step with exit code <Expected> when the publish robocopy returns <PublishExit>, as the Actions runner runs it' -ForEach @(
        @{ PublishExit = 3; Expected = 0 }
        @{ PublishExit = 16; Expected = 1 }
    ) {
        $root = Initialize-WorkFolder
        $modules = Join-Path $root 'modules'
        Add-ContentFile -Path (Join-Path $modules 'IISAdministration/IISAdministration.psm1') -Value @'
function Get-IISAppPool { [CmdletBinding()] param([string] $Name) [pscustomobject]@{ Name = $Name; ManagedRuntimeVersion = '' } }
'@
        $programFiles = Join-Path $root 'Program Files'
        Add-ContentFile -Path (Join-Path $programFiles 'IIS/Asp.Net Core Module/V2/aspnetcorev2.dll')
        $publish = Join-Path $root 'publish'
        Add-ContentFile -Path (Join-Path $publish 'TemplateName.Api.dll')
        $site = Join-Path $root 'My Site'
        Add-ContentFile -Path (Join-Path $site 'TemplateName.Api.dll')

        $step = Join-Path $root 'step.ps1'
        Set-Content -LiteralPath $step -Value @"
`$ErrorActionPreference = 'stop'
`$env:PSModulePath = '$modules' + [IO.Path]::PathSeparator + `$env:PSModulePath
`$env:ProgramFiles = '$programFiles'
`$env:ConnectionStrings__Database = 'Server=stub'
function global:dotnet { `$global:LASTEXITCODE = 0 }
function global:robocopy { if (`$args -contains '/MIR') { `$global:LASTEXITCODE = $PublishExit } else { `$global:LASTEXITCODE = 1 } }
function global:Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200 } }
& '$($script:ScriptPath)' -PublishPath '$publish' -SitePath '$site' -AppPool 'Api' -HealthUrl 'http://localhost/health/ready'
if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit `$LASTEXITCODE }
"@

        $shell = (Get-Process -Id $PID).Path
        $output = & $shell -NoProfile -NonInteractive -Command ". '$step'" 2>&1 | Out-String

        $LASTEXITCODE | Should -Be $Expected -Because $output
        if ($Expected -eq 0) { Test-Path -LiteralPath (Join-Path $site 'app_offline.htm') | Should -BeFalse }
    }
}

Describe 'Invoke-Deployment' {
    BeforeAll {
        $script:Publish = 'C:\Build\My Publish'
        $script:Site = 'C:\Sites\My App'
    }

    BeforeEach {
        $script:Calls.Clear()
        Mock Write-DeployLog { }
        Mock Test-IisPreflight { $script:Calls.Add('preflight') }
        Mock Invoke-Migration { $script:Calls.Add('migrate') }
        Mock Backup-Site { $script:Calls.Add('backup') }
        Mock Set-SiteOffline { $script:Calls.Add('offline') }
        Mock Publish-Site { $script:Calls.Add('publish') }
        Mock Set-SiteOnline { $script:Calls.Add('online') }
        Mock Wait-Healthy { $script:Calls.Add('health'); $true }
        Mock Restore-Site { $script:Calls.Add('restore') }
    }

    It 'Runs in the order preflight, migrate, backup, offline, publish, online, health' {
        Invoke-Deployment -PublishPath $script:Publish -SitePath $script:Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready'

        $script:Calls -join ',' | Should -Be 'preflight,migrate,backup,offline,publish,online,health'
        Should -Invoke Restore-Site -Times 0 -Exactly
    }

    It 'hands each step the paths it needs' {
        Invoke-Deployment -PublishPath $script:Publish -SitePath $script:Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready'

        Should -Invoke Test-IisPreflight -Times 1 -Exactly -ParameterFilter { $PublishPath -eq $script:Publish -and $AppPool -eq 'Api' }
        Should -Invoke Invoke-Migration -Times 1 -Exactly -ParameterFilter { $PublishPath -eq $script:Publish }
        Should -Invoke Backup-Site -Times 1 -Exactly -ParameterFilter { $SitePath -eq $script:Site }
        Should -Invoke Publish-Site -Times 1 -Exactly -ParameterFilter { $PublishPath -eq $script:Publish -and $SitePath -eq $script:Site }
        Should -Invoke Wait-Healthy -Times 1 -Exactly -ParameterFilter { $HealthUrl -eq 'http://localhost/health/ready' -and $TimeoutSeconds -gt 0 }
    }

    It 'A failed migration stops before backup, offline and publish and throws' {
        Mock Invoke-Migration { $script:Calls.Add('migrate'); throw 'Migration failed with exit code 1.' }

        { Invoke-Deployment -PublishPath $script:Publish -SitePath $script:Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready' } |
            Should -Throw '*Migration failed*'

        $script:Calls -join ',' | Should -Be 'preflight,migrate'
        Should -Invoke Backup-Site -Times 0 -Exactly
        Should -Invoke Set-SiteOffline -Times 0 -Exactly
        Should -Invoke Publish-Site -Times 0 -Exactly
        Should -Invoke Restore-Site -Times 0 -Exactly
    }

    It 'A failed preflight stops before anything else and throws' {
        Mock Test-IisPreflight { $script:Calls.Add('preflight'); throw 'App pool Api does not exist.' }

        { Invoke-Deployment -PublishPath $script:Publish -SitePath $script:Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready' } |
            Should -Throw '*App pool*'

        $script:Calls -join ',' | Should -Be 'preflight'
    }

    It 'leaves $LASTEXITCODE at 0 after a successful deployment, whatever robocopy left there' {
        Mock Publish-Site { $script:Calls.Add('publish'); $global:LASTEXITCODE = 3 }

        Invoke-Deployment -PublishPath $script:Publish -SitePath $script:Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready'

        $global:LASTEXITCODE | Should -Be 0
    }

    It 'hands every step the paths without a trailing separator: <Site>' -ForEach @(
        @{ Site = 'C:\Sites\App\'; Publish = 'C:\Build\publish\'; ExpectedSite = 'C:\Sites\App'; ExpectedPublish = 'C:\Build\publish' }
        @{ Site = 'C:\Sites\My App\\'; Publish = 'C:\Build Output\My Publish/'; ExpectedSite = 'C:\Sites\My App'; ExpectedPublish = 'C:\Build Output\My Publish' }
        @{ Site = '/srv/my app/'; Publish = '/tmp/publish/'; ExpectedSite = '/srv/my app'; ExpectedPublish = '/tmp/publish' }
    ) {
        Invoke-Deployment -PublishPath $Publish -SitePath $Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready'

        Should -Invoke Test-IisPreflight -Times 1 -Exactly -ParameterFilter { $PublishPath -ceq $ExpectedPublish }
        Should -Invoke Invoke-Migration -Times 1 -Exactly -ParameterFilter { $PublishPath -ceq $ExpectedPublish }
        Should -Invoke Backup-Site -Times 1 -Exactly -ParameterFilter { $SitePath -ceq $ExpectedSite }
        Should -Invoke Set-SiteOffline -Times 1 -Exactly -ParameterFilter { $SitePath -ceq $ExpectedSite }
        Should -Invoke Publish-Site -Times 1 -Exactly -ParameterFilter { $PublishPath -ceq $ExpectedPublish -and $SitePath -ceq $ExpectedSite }
        Should -Invoke Set-SiteOnline -Times 1 -Exactly -ParameterFilter { $SitePath -ceq $ExpectedSite }
    }

    It 'refuses a site path that is a root (<Site>), before it touches anything' -ForEach @(
        @{ Site = 'C:\' }, @{ Site = 'D:/' }, @{ Site = '\' }, @{ Site = '/' }
    ) {
        { Invoke-Deployment -PublishPath $script:Publish -SitePath $Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready' } |
            Should -Throw '*root*'

        $script:Calls | Should -BeNullOrEmpty
    }

    It 'A failed health check restores the previous files, then throws' {
        Mock Wait-Healthy { $script:Calls.Add('health'); $false }

        $failure = $null
        try { Invoke-Deployment -PublishPath $script:Publish -SitePath $script:Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready' }
        catch { $failure = $_ }

        # The restore is not health-checked, so the message claims the files, not a working previous version.
        $failure.Exception.Message | Should -BeLike '*health*'
        $failure.Exception.Message | Should -BeLike '*the previous files were restored*'
        $failure.Exception.Message | Should -Not -BeLike '*previous version was restored*'
        $script:Calls -join ',' | Should -Be 'preflight,migrate,backup,offline,publish,online,health,restore'
        Should -Invoke Restore-Site -Times 1 -Exactly -ParameterFilter { $SitePath -eq $script:Site }
    }

    It 'A failed publish restores the previous files, then throws' {
        Mock Publish-Site { $script:Calls.Add('publish'); throw 'robocopy failed with exit code 16.' }

        { Invoke-Deployment -PublishPath $script:Publish -SitePath $script:Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready' } |
            Should -Throw '*robocopy failed*'

        $script:Calls -join ',' | Should -Be 'preflight,migrate,backup,offline,publish,restore'
        Should -Invoke Wait-Healthy -Times 0 -Exactly
    }

    It 'A failed backup leaves the site untouched and does not restore' {
        Mock Backup-Site { $script:Calls.Add('backup'); throw 'copy failed' }

        { Invoke-Deployment -PublishPath $script:Publish -SitePath $script:Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready' } |
            Should -Throw '*copy failed*'

        $script:Calls -join ',' | Should -Be 'preflight,migrate,backup'
    }

    It 'reports a failed rollback as the error' {
        Mock Wait-Healthy { $script:Calls.Add('health'); $false }
        Mock Restore-Site { $script:Calls.Add('restore'); throw 'No previous version to restore.' }

        $failure = $null
        try { Invoke-Deployment -PublishPath $script:Publish -SitePath $script:Site -AppPool 'Api' -HealthUrl 'http://localhost/health/ready' }
        catch { $failure = $_ }

        $failure.Exception.Message | Should -BeLike '*No previous version*'
        # The reason the deployment failed is kept next to the reason the restore failed.
        $failure.Exception.Message | Should -BeLike '*health*'
    }

    It 'rejects a missing parameter before it touches anything' -ForEach @(
        @{ Name = 'PublishPath' }, @{ Name = 'SitePath' }, @{ Name = 'AppPool' }, @{ Name = 'HealthUrl' }
    ) {
        $arguments = @{ PublishPath = $script:Publish; SitePath = $script:Site; AppPool = 'Api'; HealthUrl = 'http://localhost/health/ready' }
        $arguments[$Name] = ''

        { Invoke-Deployment @arguments } | Should -Throw "*$Name*"

        $script:Calls | Should -BeNullOrEmpty
    }
}

Describe 'Test-IisPreflight' {
    BeforeEach {
        $script:Publish = Join-Path (Initialize-WorkFolder) 'publish'
        Add-ContentFile -Path (Join-Path $script:Publish 'TemplateName.Api.dll')
        Mock Write-DeployLog { }
        Mock Test-IisModuleAvailable { $true }
        Mock Test-HostingBundleInstalled { $true }
        Mock Get-AppPoolInfo { [pscustomobject]@{ Name = $AppPool; ManagedRuntimeVersion = '' } }
    }

    It 'passes when everything is in place' {
        { Test-IisPreflight -PublishPath $script:Publish -AppPool 'Api' } | Should -Not -Throw
    }

    It 'throws when the IIS administration module is missing' {
        Mock Test-IisModuleAvailable { $false }
        { Test-IisPreflight -PublishPath $script:Publish -AppPool 'Api' } | Should -Throw '*IIS administration module*'
    }

    It 'throws when the Hosting Bundle is missing' {
        Mock Test-HostingBundleInstalled { $false }
        { Test-IisPreflight -PublishPath $script:Publish -AppPool 'Api' } | Should -Throw '*Hosting Bundle*'
    }

    It 'throws when the app pool does not exist' {
        Mock Get-AppPoolInfo { $null }
        { Test-IisPreflight -PublishPath $script:Publish -AppPool 'Api' } | Should -Throw "*'Api'*does not exist*"
    }

    It 'throws when the app pool is not No Managed Code' {
        Mock Get-AppPoolInfo { [pscustomobject]@{ Name = $AppPool; ManagedRuntimeVersion = 'v4.0' } }
        { Test-IisPreflight -PublishPath $script:Publish -AppPool 'Api' } | Should -Throw '*No Managed Code*'
    }

    It 'throws when the publish path has no TemplateName.Api.dll' {
        $empty = Join-Path (Initialize-WorkFolder) 'empty'
        New-Item -ItemType Directory -Path $empty | Out-Null
        { Test-IisPreflight -PublishPath $empty -AppPool 'Api' } | Should -Throw '*TemplateName.Api.dll*'
    }
}

Describe 'Get-AppPoolInfo' {
    It 'returns nothing for an app pool that does not exist' {
        Mock Import-Module { }
        Mock Get-IISAppPool { $null }
        Get-AppPoolInfo -AppPool 'Missing' | Should -BeNullOrEmpty
    }

    It 'reports an empty runtime version for No Managed Code' {
        Mock Import-Module { }
        Mock Get-IISAppPool { [pscustomobject]@{ Name = 'Api'; ManagedRuntimeVersion = '' } }
        (Get-AppPoolInfo -AppPool 'Api').ManagedRuntimeVersion | Should -Be ''
    }

    It 'reports the runtime version of a managed pool' {
        Mock Import-Module { }
        Mock Get-IISAppPool { [pscustomobject]@{ Name = 'Api'; ManagedRuntimeVersion = 'v4.0' } }
        (Get-AppPoolInfo -AppPool 'Api').ManagedRuntimeVersion | Should -Be 'v4.0'
    }
}

Describe 'Invoke-Migration' {
    BeforeEach {
        $script:Logged.Clear()
        $script:SavedConnection = $env:ConnectionStrings__Database
        # A canary, not a credential: it must never reach a message or a log line.
        $env:ConnectionStrings__Database = 'Server=db;Password=CANARY-NOT-A-SECRET'
        Mock Write-DeployLog { $script:Logged.Add($Message) }
        Mock Invoke-Dotnet { 0 }
    }

    AfterEach {
        if ($null -eq $script:SavedConnection) { Remove-Item Env:\ConnectionStrings__Database -ErrorAction SilentlyContinue }
        else { $env:ConnectionStrings__Database = $script:SavedConnection }
    }

    It 'runs dotnet TemplateName.Api.dll migrate in the publish folder' {
        Invoke-Migration -PublishPath 'C:\Build\My Publish'

        Should -Invoke Invoke-Dotnet -Times 1 -Exactly -ParameterFilter {
            $WorkingDirectory -eq 'C:\Build\My Publish' -and ($ArgumentList -join ' ') -eq 'TemplateName.Api.dll migrate'
        }
    }

    It 'throws on a non-zero exit and never mentions the connection string' {
        Mock Invoke-Dotnet { 1 }

        $failure = $null
        try { Invoke-Migration -PublishPath 'C:\Build\My Publish' } catch { $failure = $_ }

        $failure | Should -Not -BeNullOrEmpty
        $failure.Exception.Message | Should -Match 'exit code 1'
        $failure.Exception.Message | Should -Not -Match 'CANARY'
        ($script:Logged -join "`n") | Should -Not -Match 'CANARY'
    }

    It 'never logs the connection string on success either' {
        Invoke-Migration -PublishPath 'C:\Build\My Publish'
        ($script:Logged -join "`n") | Should -Not -Match 'CANARY'
    }

    It 'throws before running anything when ConnectionStrings__Database is not set' {
        Remove-Item Env:\ConnectionStrings__Database
        { Invoke-Migration -PublishPath 'C:\Build\My Publish' } | Should -Throw '*ConnectionStrings__Database*'
        Should -Invoke Invoke-Dotnet -Times 0 -Exactly
    }
}

Describe 'Get-PreviousPath' {
    # A trailing separator once put the backup inside the site (C:\Sites\App\.previous), where /MIR then deleted it.
    It 'puts the backup of <Site> next to the site, at <Expected>' -ForEach @(
        @{ Site = 'C:\Sites\App'; Expected = 'C:\Sites\App.previous' }
        @{ Site = 'C:\Sites\App\'; Expected = 'C:\Sites\App.previous' }
        @{ Site = 'C:\Sites\App\\'; Expected = 'C:\Sites\App.previous' }
        @{ Site = 'C:\Sites\App/'; Expected = 'C:\Sites\App.previous' }
        @{ Site = 'C:\Sites\My App\'; Expected = 'C:\Sites\My App.previous' }
        @{ Site = '\\server\share\My App\'; Expected = '\\server\share\My App.previous' }
        @{ Site = '/srv/my app/'; Expected = '/srv/my app.previous' }
    ) {
        Get-PreviousPath -SitePath $Site | Should -BeExactly $Expected
    }
}

Describe 'Get-NormalizedPath' {
    It 'keeps the root <Path> as it is, so it never becomes a drive-relative path or nothing' -ForEach @(
        @{ Path = 'C:\' }, @{ Path = 'C:/' }, @{ Path = '\' }, @{ Path = '/' }
    ) {
        Get-NormalizedPath -Path $Path | Should -BeExactly $Path
    }

    It 'drops the trailing separators of <Path>' -ForEach @(
        @{ Path = 'C:\Sites\My App\'; Expected = 'C:\Sites\My App' }
        @{ Path = 'C:\Sites\App\/'; Expected = 'C:\Sites\App' }
        @{ Path = 'publish/'; Expected = 'publish' }
        @{ Path = 'C:\Sites\App'; Expected = 'C:\Sites\App' }
    ) {
        Get-NormalizedPath -Path $Path | Should -BeExactly $Expected
    }
}

Describe 'Backup-Site' {
    BeforeEach {
        Mock Write-DeployLog { }
        $script:Site = Join-Path (Initialize-WorkFolder) 'My App'
        $script:Previous = "$script:Site.previous"
        $script:RobocopyCalls = [System.Collections.Generic.List[object]]::new()
        $script:PreviousExistedAtCopy = $null
        Mock Invoke-Robocopy {
            $script:PreviousExistedAtCopy = Test-Path -LiteralPath $script:Previous
            $script:RobocopyCalls.Add($ArgumentList)
            0
        }
    }

    It 'A leftover .previous folder is replaced, not merged' {
        Add-ContentFile -Path (Join-Path $script:Site 'current.dll')
        Add-ContentFile -Path (Join-Path $script:Previous 'stale-from-an-older-release.dll')

        Backup-Site -SitePath $script:Site

        # The old folder is gone before the copy starts, so nothing of it can survive the copy.
        $script:PreviousExistedAtCopy | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $script:Previous 'stale-from-an-older-release.dll') | Should -BeFalse
        $script:RobocopyCalls.Count | Should -Be 1
    }

    It 'copies the site to {SitePath}.previous as single arguments, a path with a space included' {
        Add-ContentFile -Path (Join-Path $script:Site 'current.dll')

        Backup-Site -SitePath $script:Site

        $arguments = $script:RobocopyCalls[0]
        $arguments[0] | Should -Be $script:Site
        $arguments[1] | Should -Be $script:Previous
        $arguments[0] | Should -Match ' '
        ($arguments | Where-Object { $_ -match '"' }) | Should -BeNullOrEmpty
    }

    It 'puts the backup next to the site, not inside it, when the site path ends with a separator' {
        Add-ContentFile -Path (Join-Path $script:Site 'current.dll')

        Backup-Site -SitePath ($script:Site + [IO.Path]::DirectorySeparatorChar)

        $arguments = $script:RobocopyCalls[0]
        $arguments[0] | Should -BeExactly $script:Site
        $arguments[1] | Should -BeExactly $script:Previous
        $arguments[1] | Should -Not -BeLike "$($script:Site)$([IO.Path]::DirectorySeparatorChar)*"
    }

    It 'leaves the server-owned files out of the backup' {
        Add-ContentFile -Path (Join-Path $script:Site 'current.dll')

        Backup-Site -SitePath $script:Site

        $arguments = $script:RobocopyCalls[0]
        $arguments | Should -Contain 'appsettings.Production.json'
        $arguments | Should -Contain 'app_offline.htm'
        $arguments | Should -Contain 'logs'
    }

    It 'throws when robocopy fails' {
        Add-ContentFile -Path (Join-Path $script:Site 'current.dll')
        Mock Invoke-Robocopy { 16 }
        { Backup-Site -SitePath $script:Site } | Should -Throw '*robocopy*16*'
    }

    It 'skips the copy, and drops a stale backup, when there is no site yet (a first deployment)' {
        Add-ContentFile -Path (Join-Path $script:Previous 'stale.dll')

        Backup-Site -SitePath $script:Site

        Should -Invoke Invoke-Robocopy -Times 0 -Exactly
        Test-Path -LiteralPath $script:Previous | Should -BeFalse
    }
}

Describe 'Publish-Site' {
    BeforeEach {
        Mock Write-DeployLog { }
        $script:RobocopyCalls = [System.Collections.Generic.List[object]]::new()
        Mock Invoke-Robocopy { $script:RobocopyCalls.Add($ArgumentList); 1 }
    }

    It 'Paths with spaces reach robocopy as single arguments' {
        Publish-Site -PublishPath 'C:\Build Output\My Publish' -SitePath 'C:\Sites\My App'

        $arguments = $script:RobocopyCalls[0]
        $arguments[0] | Should -BeExactly 'C:\Build Output\My Publish'
        $arguments[1] | Should -BeExactly 'C:\Sites\My App'
        # PowerShell quotes an argument with a space when it starts the process; a quote added here would be doubled.
        ($arguments | Where-Object { $_ -match '"' }) | Should -BeNullOrEmpty
        # The split halves of either path must not appear as arguments of their own.
        $arguments | Should -Not -Contain 'C:\Sites\My'
        $arguments | Should -Not -Contain 'App'
    }

    It 'drops a trailing backslash, which would escape the closing quote of the argument' {
        Publish-Site -PublishPath 'C:\Build Output\My Publish\' -SitePath 'C:\Sites\My App\'

        $arguments = $script:RobocopyCalls[0]
        $arguments[0] | Should -BeExactly 'C:\Build Output\My Publish'
        $arguments[1] | Should -BeExactly 'C:\Sites\My App'
    }

    It 'Publish-Site keeps the server-owned files' {
        Publish-Site -PublishPath 'C:\Build\publish' -SitePath 'C:\Sites\App'

        $arguments = [string[]]$script:RobocopyCalls[0]
        $arguments | Should -Contain '/MIR'

        $excludeFiles = $arguments.IndexOf('/XF')
        $excludeDirectories = $arguments.IndexOf('/XD')
        $excludeFiles | Should -BeGreaterThan 1
        $excludeDirectories | Should -BeGreaterThan 1

        # Files named after /XF (up to the next switch) and directories named after /XD.
        $files = $arguments[($excludeFiles + 1)..($arguments.Length - 1)] | Where-Object { $_ -notmatch '^/' } |
            Select-Object -First 5
        $directories = $arguments[($excludeDirectories + 1)..($arguments.Length - 1)] | Where-Object { $_ -notmatch '^/' } |
            Select-Object -First 5
        $files | Should -Contain 'appsettings.Production.json'
        $files | Should -Contain 'app_offline.htm'
        $directories | Should -Contain 'logs'
    }

    It 'treats robocopy exit code <Code> (below 8) as success' -ForEach @(
        @{ Code = 0 }, @{ Code = 1 }, @{ Code = 2 }, @{ Code = 3 }, @{ Code = 5 }, @{ Code = 7 }
    ) {
        $script:RobocopyExit = $Code
        Mock Invoke-Robocopy { $script:RobocopyExit }
        { Publish-Site -PublishPath 'C:\Build\publish' -SitePath 'C:\Sites\App' } | Should -Not -Throw
    }

    It 'treats robocopy exit code <Code> (8 and above) as failure' -ForEach @(
        @{ Code = 8 }, @{ Code = 9 }, @{ Code = 16 }
    ) {
        $script:RobocopyExit = $Code
        Mock Invoke-Robocopy { $script:RobocopyExit }
        { Publish-Site -PublishPath 'C:\Build\publish' -SitePath 'C:\Sites\App' } | Should -Throw "*exit code $Code*"
    }
}

Describe 'Set-SiteOffline and Set-SiteOnline' {
    BeforeEach {
        Mock Write-DeployLog { }
        $script:Site = Join-Path (Initialize-WorkFolder) 'My App'
        $script:OfflineFile = Join-Path $script:Site 'app_offline.htm'
    }

    It 'writes app_offline.htm and removes it again' {
        Add-ContentFile -Path (Join-Path $script:Site 'web.config')

        Set-SiteOffline -SitePath $script:Site
        Test-Path -LiteralPath $script:OfflineFile | Should -BeTrue
        (Get-Content -LiteralPath $script:OfflineFile -Raw).Length | Should -BeGreaterThan 0

        Set-SiteOnline -SitePath $script:Site
        Test-Path -LiteralPath $script:OfflineFile | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $script:Site 'web.config') | Should -BeTrue
    }

    It 'creates the site folder for a first deployment' {
        Set-SiteOffline -SitePath $script:Site
        Test-Path -LiteralPath $script:OfflineFile | Should -BeTrue
    }

    It 'goes online without an error when it is already online' {
        New-Item -ItemType Directory -Path $script:Site | Out-Null
        { Set-SiteOnline -SitePath $script:Site } | Should -Not -Throw
    }
}

Describe 'Restore-Site' {
    BeforeEach {
        $script:Calls.Clear()
        Mock Write-DeployLog { }
        Mock Set-SiteOffline { $script:Calls.Add('offline') }
        Mock Set-SiteOnline { $script:Calls.Add('online') }
        $script:RobocopyCalls = [System.Collections.Generic.List[object]]::new()
        Mock Invoke-Robocopy { $script:Calls.Add('robocopy'); $script:RobocopyCalls.Add($ArgumentList); 1 }
        $script:Site = Join-Path (Initialize-WorkFolder) 'My App'
        $script:Previous = "$script:Site.previous"
    }

    It 'mirrors {SitePath}.previous over the site while the site is offline' {
        Add-ContentFile -Path (Join-Path $script:Previous 'old.dll')

        Restore-Site -SitePath $script:Site

        $script:Calls -join ',' | Should -Be 'offline,robocopy,online'
        $arguments = [string[]]$script:RobocopyCalls[0]
        $arguments[0] | Should -BeExactly $script:Previous
        $arguments[1] | Should -BeExactly $script:Site
        $arguments | Should -Contain '/MIR'
        $arguments | Should -Contain 'appsettings.Production.json'
        $arguments | Should -Contain 'app_offline.htm'
        $arguments | Should -Contain 'logs'
    }

    It 'restores from the folder next to the site when the site path ends with a separator' {
        Add-ContentFile -Path (Join-Path $script:Previous 'old.dll')

        Restore-Site -SitePath ($script:Site + [IO.Path]::DirectorySeparatorChar)

        $arguments = [string[]]$script:RobocopyCalls[0]
        $arguments[0] | Should -BeExactly $script:Previous
        $arguments[1] | Should -BeExactly $script:Site
    }

    It 'throws when there is no previous version, and leaves the site as it is' {
        { Restore-Site -SitePath $script:Site } | Should -Throw '*previous*'
        $script:Calls | Should -BeNullOrEmpty
    }

    It 'leaves the site offline, not half restored and online, when robocopy fails' {
        Add-ContentFile -Path (Join-Path $script:Previous 'old.dll')
        Mock Invoke-Robocopy { $script:Calls.Add('robocopy'); 16 }

        { Restore-Site -SitePath $script:Site } | Should -Throw '*robocopy*16*'

        $script:Calls -join ',' | Should -Be 'offline,robocopy'
    }
}

Describe 'Wait-Healthy' {
    BeforeEach {
        Mock Write-DeployLog { }
        Mock Start-Sleep { }
    }

    It 'returns $true as soon as the endpoint answers' {
        $script:Answers = [System.Collections.Generic.Queue[bool]]::new([bool[]]@($false, $false, $true))
        Mock Test-HealthEndpoint { $script:Answers.Dequeue() }

        Wait-Healthy -HealthUrl 'http://localhost/health/ready' -TimeoutSeconds 30 | Should -BeTrue

        Should -Invoke Test-HealthEndpoint -Times 3 -Exactly
    }

    It 'returns $false when the time is up' {
        Mock Test-HealthEndpoint { $false }

        Wait-Healthy -HealthUrl 'http://localhost/health/ready' -TimeoutSeconds 0 | Should -BeFalse

        Should -Invoke Test-HealthEndpoint -Times 1 -Exactly
    }
}

Describe 'Test-HealthEndpoint' {
    It 'is healthy for a 2xx answer' {
        Mock Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200 } }
        Test-HealthEndpoint -Url 'http://localhost/health/ready' | Should -BeTrue
    }

    It 'is unhealthy for any other status' {
        Mock Invoke-WebRequest { [pscustomobject]@{ StatusCode = 503 } }
        Test-HealthEndpoint -Url 'http://localhost/health/ready' | Should -BeFalse
    }

    It 'is unhealthy when the request throws' {
        Mock Invoke-WebRequest { throw 'connection refused' }
        Test-HealthEndpoint -Url 'http://localhost/health/ready' | Should -BeFalse
    }

    It 'allows TLS 1.2 before the request (an https health URL under Windows PowerShell 5.1)' {
        Mock Enable-Tls12 { }
        Mock Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200 } }

        Test-HealthEndpoint -Url 'https://api.example.test/health/ready' | Should -BeTrue

        Should -Invoke Enable-Tls12 -Times 1 -Exactly
    }
}

Describe 'Enable-Tls12' {
    BeforeEach {
        $script:SavedProtocol = [Net.ServicePointManager]::SecurityProtocol
    }

    AfterEach {
        [Net.ServicePointManager]::SecurityProtocol = $script:SavedProtocol
    }

    It 'adds TLS 1.2 on Windows PowerShell and keeps what was allowed' {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls

        Enable-Tls12 -Edition 'Desktop'

        $protocol = [Net.ServicePointManager]::SecurityProtocol
        ($protocol -band [Net.SecurityProtocolType]::Tls12) | Should -Be ([Net.SecurityProtocolType]::Tls12)
        ($protocol -band [Net.SecurityProtocolType]::Tls) | Should -Be ([Net.SecurityProtocolType]::Tls)
    }

    It 'leaves the system default (0) alone on Windows PowerShell: the OS already offers TLS 1.2' {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]0

        Enable-Tls12 -Edition 'Desktop'

        [int][Net.ServicePointManager]::SecurityProtocol | Should -Be 0
    }

    It 'changes nothing on PowerShell 7' {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls

        Enable-Tls12 -Edition 'Core'

        [Net.ServicePointManager]::SecurityProtocol | Should -Be ([Net.SecurityProtocolType]::Tls)
    }

    It 'runs in the current shell without an error' {
        { Enable-Tls12 } | Should -Not -Throw
    }
}

Describe 'Publish, backup and restore with a real robocopy' -Skip:(-not $script:OnWindows) {
    BeforeAll {
        Mock Write-DeployLog { }
    }

    It 'swaps a site whose path holds a space, keeps the server-owned files and restores the previous version' {
        $site = Join-Path (Initialize-WorkFolder) 'My App'
        $publish = Join-Path (Split-Path -Parent $site) 'New Build'
        Add-ContentFile -Path (Join-Path $site 'TemplateName.Api.dll') -Value 'old'
        Add-ContentFile -Path (Join-Path $site 'removed-in-new-release.dll') -Value 'old'
        Add-ContentFile -Path (Join-Path $site 'appsettings.Production.json') -Value 'server-owned'
        Add-ContentFile -Path (Join-Path $site 'logs\today.log') -Value 'server-owned'
        Add-ContentFile -Path (Join-Path $publish 'TemplateName.Api.dll') -Value 'new'
        Add-ContentFile -Path (Join-Path $publish 'added-in-new-release.dll') -Value 'new'
        Add-ContentFile -Path "$site.previous\leftover.dll" -Value 'stale'

        Backup-Site -SitePath $site
        Test-Path -LiteralPath "$site.previous\leftover.dll" | Should -BeFalse
        Get-Content -LiteralPath "$site.previous\TemplateName.Api.dll" | Should -Be 'old'
        Test-Path -LiteralPath "$site.previous\appsettings.Production.json" | Should -BeFalse

        Set-SiteOffline -SitePath $site
        Publish-Site -PublishPath $publish -SitePath $site
        Set-SiteOnline -SitePath $site

        Get-Content -LiteralPath (Join-Path $site 'TemplateName.Api.dll') | Should -Be 'new'
        Test-Path -LiteralPath (Join-Path $site 'added-in-new-release.dll') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $site 'removed-in-new-release.dll') | Should -BeFalse
        Get-Content -LiteralPath (Join-Path $site 'appsettings.Production.json') | Should -Be 'server-owned'
        Get-Content -LiteralPath (Join-Path $site 'logs\today.log') | Should -Be 'server-owned'
        Test-Path -LiteralPath (Join-Path $site 'app_offline.htm') | Should -BeFalse

        Restore-Site -SitePath $site
        Get-Content -LiteralPath (Join-Path $site 'TemplateName.Api.dll') | Should -Be 'old'
        Test-Path -LiteralPath (Join-Path $site 'added-in-new-release.dll') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $site 'removed-in-new-release.dll') | Should -BeTrue
        Get-Content -LiteralPath (Join-Path $site 'appsettings.Production.json') | Should -Be 'server-owned'
        Test-Path -LiteralPath (Join-Path $site 'app_offline.htm') | Should -BeFalse
    }
}
