#requires -Version 5.1
<#
.SYNOPSIS
Installs a verified MusicMachine Windows x64 release for the current user.
.DESCRIPTION
Downloads release metadata and the Windows setup from the official repository,
checks its exact version, byte length and SHA-256, then runs the per-user setup.
No elevation, credential storage or PowerShell execution-policy change is used.
Checksums verify integrity against the official release, not Authenticode signing.
.PARAMETER Version
Stable version to install, such as 0.1.0; defaults to the latest published release.
.PARAMETER InstallDirectory
Optional absolute installation directory. By default the setup uses its saved
location or LocalAppData\Programs\MusicMachine.
.PARAMETER Silent
Runs the setup silently. Without this switch the normal installer UI is shown.
.EXAMPLE
.\install-musicmachine.ps1
.EXAMPLE
.\install-musicmachine.ps1 -Version 0.1.0 -Silent
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidatePattern('^(latest|\d+\.\d+\.\d+)$')]
    [string] $Version = 'latest',
    [string] $InstallDirectory,
    [switch] $Silent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Test-MusicMachineWindows {
    return $env:OS -eq 'Windows_NT' -and [Environment]::Is64BitOperatingSystem
}
function Test-MusicMachineAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        return $identity.IsSystem -or $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } finally { $identity.Dispose() }
}
function Assert-MusicMachineUserContext {
    if (-not (Test-MusicMachineWindows)) { throw 'This installer requires 64-bit Windows.' }
    # Check the effective token, not account membership: a normal UAC-filtered
    # administrator is allowed. Errors inspecting the token fail closed.
    if (Test-MusicMachineAdministrator) {
        throw 'Run MusicMachine setup from a normal, non-administrator PowerShell window. Elevated installation is not supported.'
    }
}
function Resolve-MusicMachineInstallDirectory([string] $Path) {
    # IsPathRooted alone accepts C:relative and \root-relative paths on Windows.
    # Permit only drive-absolute and ordinary UNC paths; reject device namespaces.
    if ($Path -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\/:*?"<>|]+\\[^\\/:*?"<>|]+(?:\\|$))' -or
        $Path -match '[\x00-\x1f"<>|]' -or $Path -match '^\\\\[?.]\\') {
        throw 'InstallDirectory must be an absolute Windows path without quotes, control characters or device prefixes.'
    }
    return [IO.Path]::GetFullPath($Path)
}
function New-MusicMachineHttpClient {
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseDefaultCredentials = $false
    $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    return $client
}
function Assert-MusicMachineDownloadUri([Uri] $Uri) {
    $allowed = @('github.com', 'api.github.com', 'release-assets.githubusercontent.com',
        'objects.githubusercontent.com', 'github-releases.githubusercontent.com')
    if (-not $Uri.IsAbsoluteUri -or $Uri.Scheme -cne 'https' -or $Uri.Port -ne 443 -or
        $Uri.UserInfo -or $Uri.Fragment -or $allowed -notcontains $Uri.DnsSafeHost) {
        throw 'Download redirect is outside the allowed HTTPS GitHub release hosts.'
    }
}
function Receive-MusicMachineRelease {
    param([Uri] $Uri, [long] $MaximumBytes, [int] $TimeoutSeconds, [string] $OutFile)
    if ($MaximumBytes -le 0 -or $MaximumBytes -gt 536870912 -or $TimeoutSeconds -le 0) {
        throw 'Invalid download limits.'
    }
    $client = $null; $deadline = $null; $response = $null; $request = $null; $input = $null; $output = $null
    $completed = $false; $createdOutput = $false; $cancelRead = $null
    try {
        $client = New-MusicMachineHttpClient
        # One deadline covers headers, every redirect and every body read.
        $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
        for ($redirect = 0; ; $redirect++) {
            Assert-MusicMachineDownloadUri $Uri
            $deadline.Token.ThrowIfCancellationRequested()
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
            $request.Headers.UserAgent.ParseAdd('MusicMachineInstaller')
            $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
            $status = [int]$response.StatusCode
            if ($status -in @(301, 302, 303, 307, 308)) {
                if ($redirect -ge 5 -or $null -eq $response.Headers.Location) {
                    throw 'Download redirect limit exceeded or Location is missing.'
                }
                $Uri = [Uri]::new($Uri, $response.Headers.Location)
                # Validate before issuing the next request, including relative redirects.
                Assert-MusicMachineDownloadUri $Uri
                $response.Dispose(); $response = $null
                $request.Dispose(); $request = $null
                continue
            }
            if ($status -ne 200) { throw "Release download failed with HTTP $status." }
            break
        }
        $declaredLength = $response.Content.Headers.ContentLength
        if ($null -ne $declaredLength -and ($declaredLength -lt 0 -or $declaredLength -gt $MaximumBytes)) {
            throw 'Download exceeds the permitted byte limit.'
        }
        if ($OutFile -and $null -ne $declaredLength -and $declaredLength -ne $MaximumBytes) {
            throw 'Installer size mismatch. Setup was not run.'
        }
        # Compression is not requested, and encoded bodies are rejected so byte
        # limits and hashes always apply to the actual published file.
        if ($response.Content.Headers.ContentEncoding.Count -ne 0) { throw 'Encoded release responses are not supported.' }
        $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        # Some Windows PowerShell 5.1 streams do not observe cancellation after
        # a read has started. Bind Dispose directly (no runspace callback) so the
        # overall deadline also aborts those blocking body reads.
        $cancelRead = $deadline.Token.Register([Action][Delegate]::CreateDelegate([Action], $input, 'Dispose'))
        if ($OutFile) {
            $output = [IO.File]::Open($OutFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $createdOutput = $true
        } else { $output = [IO.MemoryStream]::new() }
        $buffer = [byte[]]::new(65536)
        [long]$total = 0
        while ($true) {
            $deadline.Token.ThrowIfCancellationRequested()
            # Read at most the remainder plus one byte to detect an oversized
            # chunked/lying response without buffering or writing its remainder.
            $count = [int][Math]::Min($buffer.Length, $MaximumBytes - $total + 1)
            $read = $input.ReadAsync($buffer, 0, $count, $deadline.Token).GetAwaiter().GetResult()
            if ($read -eq 0) { break }
            $total += $read
            if ($total -gt $MaximumBytes) { throw 'Download exceeds the permitted byte limit.' }
            $output.Write($buffer, 0, $read)
        }
        $deadline.Token.ThrowIfCancellationRequested()
        if (($null -ne $declaredLength -and $total -ne $declaredLength) -or ($OutFile -and $total -ne $MaximumBytes)) {
            throw 'Download size mismatch. Setup was not run.'
        }
        if (-not $OutFile) {
            # Strict UTF-8 prevents malformed bytes being silently replaced.
            $utf8 = [Text.UTF8Encoding]::new($false, $true)
            $text = $utf8.GetString($output.ToArray())
            if ($text.Length -gt 0 -and $text[0] -eq [char]0xfeff) { $text = $text.Substring(1) }
            $text
        }
        $completed = $true
    } finally {
        if ($null -ne $cancelRead) { $cancelRead.Dispose() }
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $input) { $input.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $request) { $request.Dispose() }
        if ($null -ne $client) { $client.Dispose() }
        if ($null -ne $deadline) { $deadline.Dispose() }
        if (-not $completed -and $createdOutput -and (Test-Path -LiteralPath $OutFile)) {
            Remove-Item -LiteralPath $OutFile -Force
        }
    }
}
function Invoke-MusicMachineInstall {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidatePattern('^(latest|\d+\.\d+\.\d+)$')][string] $Version = 'latest',
        [string] $InstallDirectory, [switch] $Silent
    )
    Assert-MusicMachineUserContext
    if ($InstallDirectory) { $InstallDirectory = Resolve-MusicMachineInstallDirectory $InstallDirectory }
    $repository = 'isaiahpettingill/MusicMachine'
    $releaseRoot = "https://github.com/$repository/releases/"
    $manifestUrl = if ($Version -eq 'latest') { $releaseRoot + 'latest/download/release.json' }
        else { $releaseRoot + "download/v$Version/release.json" }
    $manifestText = Receive-MusicMachineRelease -Uri $manifestUrl -MaximumBytes 1048576 -TimeoutSeconds 60
    try { $manifest = ConvertFrom-Json -InputObject $manifestText }
    catch { throw 'The official release manifest is not valid JSON. Setup was not run.' }
    if ($manifest.schema -ne 1 -or $manifest.product -ne 'MusicMachine' -or
        [string]$manifest.version -cnotmatch '^\d+\.\d+\.\d+$' -or [string]$manifest.commit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'The official release manifest is invalid.'
    }
    if ($Version -ne 'latest' -and $manifest.version -ne $Version) { throw 'The release manifest does not match the requested version.' }
    $releaseVersion = [string]$manifest.version
    $assetName = 'MusicMachine-win-x64-setup.exe'
    $assets = @($manifest.assets | Where-Object { $_.name -ceq $assetName })
    if ($assets.Count -ne 1) { throw 'The release must contain exactly one Windows setup.' }
    $asset = $assets[0]
    $expectedUrl = $releaseRoot + "download/v$releaseVersion/$assetName"
    if (-not [string]::Equals([string]$asset.url, $expectedUrl, [StringComparison]::Ordinal) -or
        [string]$asset.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        ($asset.size -isnot [int] -and $asset.size -isnot [long]) -or $asset.size -le 0 -or $asset.size -gt 536870912) {
        throw 'Windows setup metadata is invalid or points outside the official versioned release.'
    }
    if (-not $PSCmdlet.ShouldProcess("MusicMachine $releaseVersion for the current Windows user", 'Install')) { return }
    $downloadDirectory = Join-Path ([IO.Path]::GetTempPath()) ('MusicMachine-install-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $downloadDirectory)
    try {
        $installer = Join-Path $downloadDirectory $assetName
        Write-Host "Downloading MusicMachine $releaseVersion..."
        Receive-MusicMachineRelease -Uri $expectedUrl -MaximumBytes $asset.size -TimeoutSeconds 300 -OutFile $installer
        if ((Get-Item -LiteralPath $installer).Length -ne $asset.size) { throw 'Installer size mismatch. Setup was not run.' }
        $actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, [string]$asset.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Installer SHA-256 mismatch. Setup was not run.'
        }
        # Recheck immediately before launching; there is no elevation verb or bypass.
        Assert-MusicMachineUserContext
        if ($Silent) { Write-Host 'Download verified. Installing MusicMachine...' }
        else { Write-Host 'Download verified. Opening MusicMachine setup...' }
        $arguments = if ($Silent) { '/S' } else { '' }
        if ($InstallDirectory) {
            # NSIS requires /D= to be the final, unquoted command-line argument.
            $arguments = ($arguments + ' /D=' + $InstallDirectory).TrimStart()
        }
        $start = @{ FilePath = $installer; Wait = $true; PassThru = $true }
        if ($arguments) { $start.ArgumentList = $arguments }
        $process = Start-Process @start
        if ($process.ExitCode -eq 1) { throw 'Setup was canceled. Run the script again when you are ready to install.' }
        if ($process.ExitCode -eq 2) { throw 'Setup could not finish. Follow the message in the setup window, then try again.' }
        if ($process.ExitCode -ne 0) { throw "Setup could not finish (exit code $($process.ExitCode)). Try running the Windows setup again." }
        Write-Host "MusicMachine $releaseVersion setup finished."
    } finally {
        if (Test-Path -LiteralPath $downloadDirectory) { Remove-Item -LiteralPath $downloadDirectory -Recurse -Force }
    }
}

try {
    Invoke-MusicMachineInstall @PSBoundParameters
} catch {
    # Keep the normal console useful: PowerShell's default error renderer adds
    # source lines, carets and type names to even a simple canceled installation.
    # The base exception preserves the actual failure without that stack dump.
    Write-Host ('MusicMachine setup stopped: ' + $_.Exception.GetBaseException().Message) -ForegroundColor Red
    exit 1
}
