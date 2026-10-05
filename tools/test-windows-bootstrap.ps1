# Isolated source-level tests: fake HTTP streams and fake process launches only.
# Runs under Windows PowerShell 5.1 or pwsh, including elevated CI. Definitions
# are imported through the AST; the production entry point is never bypassed or
# executed. Windows-only identity/path primitives are explicitly mocked here.
#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$references = @(if ($PSVersionTable.PSEdition -eq 'Desktop') { 'System.Net.Http.dll' })
$source = @'
using System;
using System.IO;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
public sealed class FixtureStream : Stream {
    public byte[] Data; public int PositionValue; public int TotalRead; public bool Closed;
    public int CancelAfter = -1; public bool CancelRead; public bool StallRead; public bool IgnoreReadCancellation; public int ChunkSize = 65536;
    TaskCompletionSource<bool> disposed = new TaskCompletionSource<bool>();
    public FixtureStream(byte[] data) { Data = data; }
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { return PositionValue; } set { throw new NotSupportedException(); } }
    public override int Read(byte[] b, int offset, int count) {
        int n = Math.Min(Math.Min(count, Data.Length - PositionValue), ChunkSize);
        Array.Copy(Data, PositionValue, b, offset, n); PositionValue += n; TotalRead += n; return n;
    }
    public override async Task<int> ReadAsync(byte[] b, int offset, int count, CancellationToken token) {
        if (!token.CanBeCanceled) throw new Exception("Body read omitted cancellation token");
        if (CancelRead || (CancelAfter >= 0 && PositionValue >= CancelAfter)) throw new OperationCanceledException("Simulated cancellation");
        if (StallRead) { if (IgnoreReadCancellation) await disposed.Task; else await Task.Delay(Timeout.Infinite, token); }
        token.ThrowIfCancellationRequested(); return Read(b, offset, count);
    }
    protected override void Dispose(bool disposing) { Closed = true; disposed.TrySetResult(true); base.Dispose(disposing); }
    public override void Flush() {}
    public override long Seek(long a, SeekOrigin b) { throw new NotSupportedException(); }
    public override void SetLength(long a) { throw new NotSupportedException(); }
    public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
}
public sealed class FixtureState {
    public Queue<HttpResponseMessage> Responses = new Queue<HttpResponseMessage>();
    public List<string> Requested = new List<string>(); public int DisposedClients;
    public bool NetworkError; public bool StallHeaders; public int HeaderDelayMilliseconds;
}
public sealed class FixtureHandler : HttpMessageHandler {
    FixtureState state;
    public FixtureHandler(FixtureState s) { state = s; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        if (!token.CanBeCanceled) throw new Exception("Request omitted cancellation token");
        state.Requested.Add(request.RequestUri.AbsoluteUri);
        if (state.NetworkError) throw new IOException("Simulated network failure");
        if (state.StallHeaders) await Task.Delay(Timeout.Infinite, token);
        if (state.HeaderDelayMilliseconds > 0) await Task.Delay(state.HeaderDelayMilliseconds, token);
        token.ThrowIfCancellationRequested();
        if (state.Responses.Count == 0) throw new Exception("Unexpected request");
        return state.Responses.Dequeue();
    }
    protected override void Dispose(bool disposing) { state.DisposedClients++; base.Dispose(disposing); }
}
'@
if ($references.Count) { Add-Type -TypeDefinition $source -ReferencedAssemblies $references }
else { Add-Type -TypeDefinition $source }
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'install-windows.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($definition in $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] }) {
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$realPathResolver = ${function:Resolve-MusicMachineInstallDirectory}
$realClientFactory = ${function:New-MusicMachineHttpClient}
function Assert-True([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Test-MusicMachineWindows { return $script:fixture.Windows }
function Test-MusicMachineAdministrator {
    if ($script:fixture.TokenError) { throw 'Token inspection failed' }
    $script:fixture.TokenChecks++
    return $script:fixture.Administrator -or ($script:fixture.ElevateBeforeLaunch -and $script:fixture.TokenChecks -gt 1)
}
function Resolve-MusicMachineInstallDirectory([string] $Path) {
    # Exercise the original validation, but emulate normalization on Linux only.
    $resolved = & $script:realPathResolver $Path
    if ($env:OS -eq 'Windows_NT') { return $resolved }
    return $Path
}
function New-MusicMachineHttpClient {
    $client = [Net.Http.HttpClient]::new([FixtureHandler]::new($script:fixture.Transport))
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    return $client
}
function Start-Process {
    param($FilePath, [switch] $Wait, [switch] $PassThru, $ArgumentList)
    Assert-True ($Wait -and $PassThru) 'Setup must wait and inspect its result'
    $script:fixture.Started.Add([pscustomobject]@{ FilePath = $FilePath; Arguments = $ArgumentList })
    if ($script:fixture.LaunchError) { throw 'Simulated launch failure' }
    return [pscustomobject]@{ ExitCode = $script:fixture.ExitCode }
}
function New-Fixture {
    $bytes = [Text.Encoding]::UTF8.GetBytes('harmless installer fixture')
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $script:fixture = @{
        Payload = $bytes; Windows = $true; Administrator = $false; TokenError = $false
        TokenChecks = 0; ElevateBeforeLaunch = $false; LaunchError = $false; ExitCode = 0
        Manifest = [pscustomobject]@{
            schema = 1; product = 'MusicMachine'; version = '1.2.3'; commit = ('a' * 40)
            assets = @([pscustomobject]@{
                name = 'MusicMachine-win-x64-setup.exe'; size = $bytes.Length; sha256 = $hash
                url = 'https://github.com/isaiahpettingill/MusicMachine/releases/download/v1.2.3/MusicMachine-win-x64-setup.exe'
            })
        }
        Started = [Collections.Generic.List[object]]::new()
        Transport = [FixtureState]::new()
        Streams = [Collections.Generic.List[FixtureStream]]::new()
    }
}
function Add-Response([byte[]] $Bytes, [int] $Status = 200, [string] $Location = '', [long] $Length = -1) {
    $stream = [FixtureStream]::new($Bytes)
    $script:fixture.Streams.Add($stream)
    # .NET Framework's enum predates HTTP 308, but HttpResponseMessage supports
    # that numeric status. Avoid PowerShell's stricter named-enum conversion.
    $response = [Net.Http.HttpResponseMessage]::new([Enum]::ToObject([Net.HttpStatusCode], $Status))
    $response.Content = [Net.Http.StreamContent]::new($stream)
    if ($Length -ge 0) { $response.Content.Headers.ContentLength = $Length }
    if ($Location) { $response.Headers.Location = [Uri]::new($Location, [UriKind]::RelativeOrAbsolute) }
    $script:fixture.Transport.Responses.Enqueue($response)
    return $response
}
function Add-Defaults {
    [void](Add-Response ([Text.Encoding]::UTF8.GetBytes(($script:fixture.Manifest | ConvertTo-Json -Depth 6))))
    [void](Add-Response $script:fixture.Payload)
}
$script:checks = 0
function Expect-Result([ScriptBlock] $Action, [string] $ExpectedError = '') {
    $problem = $null
    try { & $Action *> $null } catch { $problem = $_.Exception.Message }
    if ($ExpectedError) {
        Assert-True ($null -ne $problem -and $problem -match $ExpectedError) "Expected '$ExpectedError', got '$problem'"
    } else { Assert-True ($null -eq $problem) "Unexpected failure: $problem" }
    $script:checks++
}
function Invoke-Fixture([hashtable] $Arguments = @{}, [string] $ExpectedError = '') {
    if (-not $script:fixture.Transport.Responses.Count) { Add-Defaults }
    $before = @(Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'MusicMachine-install-*' | ForEach-Object FullName)
    Expect-Result { Invoke-MusicMachineInstall @Arguments -Confirm:$false } $ExpectedError
    $after = @(Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'MusicMachine-install-*' | ForEach-Object FullName)
    Assert-True (@($after | Where-Object { $before -notcontains $_ }).Count -eq 0) 'Temporary download folder was retained'
    if ($ExpectedError) { Assert-True ($script:fixture.Started.Count -eq 0 -or $script:fixture.ExitCode -ne 0 -or $script:fixture.LaunchError) 'Unverified setup was launched' }
}

# Main bootstrap flow, with the real bounded transport and real file/hash checks.
New-Fixture; Invoke-Fixture
Assert-True ($fixture.Started.Count -eq 1 -and $fixture.TokenChecks -eq 2) 'Valid setup or pre-launch token check missing'
Assert-True ($fixture.Transport.Requested[0] -match '/releases/latest/download/release.json$') 'Latest manifest URL changed'
New-Fixture; Invoke-Fixture @{ Version = '1.2.3'; Silent = $true; InstallDirectory = 'C:\A path with spaces\MusicMachine' }
Assert-True ($fixture.Started[0].Arguments -ceq '/S /D=C:\A path with spaces\MusicMachine') 'NSIS final /D was quoted or split'
Assert-True ($fixture.Transport.Requested[0] -match '/releases/download/v1.2.3/release.json$') 'Pinned URL changed'
New-Fixture; Invoke-Fixture @{ InstallDirectory = 'C:\MusicMachine' }
Assert-True ($fixture.Started[0].Arguments -ceq '/D=C:\MusicMachine') 'Non-silent directory arguments changed'
New-Fixture; Invoke-Fixture @{ WhatIf = $true }
Assert-True ($fixture.Transport.Requested.Count -eq 1 -and $fixture.Started.Count -eq 0) 'WhatIf downloaded or launched setup'
foreach ($path in @('relative\MusicMachine', 'C:MusicMachine', '\MusicMachine', '\\?\C:\MusicMachine', '\\.\C:\MusicMachine', 'C:\Bad"path', "C:\Bad`npath", "C:\Bad`0path")) {
    New-Fixture; Invoke-Fixture @{ InstallDirectory = $path } 'absolute Windows path'
    Assert-True ($fixture.Transport.Requested.Count -eq 0) 'Invalid path fetched metadata'
}
foreach ($guard in @('Administrator', 'TokenError', 'ElevateBeforeLaunch')) {
    New-Fixture; $fixture[$guard] = $true
    Invoke-Fixture @{} $(if ($guard -eq 'TokenError') { 'Token inspection failed' } else { 'non-administrator' })
    if ($guard -ne 'ElevateBeforeLaunch') { Assert-True ($fixture.Transport.Requested.Count -eq 0) 'Unsafe token fetched metadata' }
}
New-Fixture; $fixture.Windows = $false; Invoke-Fixture @{} '64-bit Windows'
New-Fixture; $fixture.Payload = [Text.Encoding]::UTF8.GetBytes('short'); Invoke-Fixture @{} 'size mismatch'
New-Fixture; $fixture.Payload = [byte[]]::new(100000); Invoke-Fixture @{} 'byte limit'
Assert-True ($fixture.Streams[1].TotalRead -eq $fixture.Manifest.assets[0].size + 1) 'Oversized package was read past its limit'
New-Fixture; $fixture.Manifest.assets[0].sha256 = '0' * 64; Invoke-Fixture @{} 'SHA-256 mismatch'
foreach ($url in @('https://example.invalid/setup.exe', 'http://github.com/isaiahpettingill/MusicMachine/releases/download/v1.2.3/MusicMachine-win-x64-setup.exe', 'https://github.com/isaiahpettingill/MusicMachine/releases/latest/download/MusicMachine-win-x64-setup.exe')) {
    New-Fixture; $fixture.Manifest.assets[0].url = $url; Invoke-Fixture @{} 'metadata is invalid'
    Assert-True ($fixture.Transport.Requested.Count -eq 1) 'Untrusted URL was fetched'
}
foreach ($size in @(0, -1, 536870913, 1.5, '25')) {
    New-Fixture; $fixture.Manifest.assets[0].size = $size; Invoke-Fixture @{} 'metadata is invalid'
}
New-Fixture; $fixture.Manifest.assets += $fixture.Manifest.assets[0]; Invoke-Fixture @{} 'exactly one Windows setup'
New-Fixture; $fixture.Manifest.assets = @(); Invoke-Fixture @{} 'exactly one Windows setup'
New-Fixture; Invoke-Fixture @{ Version = '1.2.4' } 'does not match'
New-Fixture; $fixture.ExitCode = 1223; Invoke-Fixture @{} 'exited with code 1223'
New-Fixture; $fixture.LaunchError = $true; Invoke-Fixture @{} 'Simulated launch failure'
New-Fixture; $fixture.Transport.NetworkError = $true; Invoke-Fixture @{} 'Simulated network failure'
New-Fixture; [void](Add-Response ([byte[]]::new(1048577))); Invoke-Fixture @{} 'byte limit'
Assert-True ($fixture.Streams[0].TotalRead -eq 1048577) 'Metadata read did not stop at the byte cap'
foreach ($invalidJson in @('{broken', '{"schema":')) {
    New-Fixture; [void](Add-Response ([Text.Encoding]::UTF8.GetBytes($invalidJson)))
    Invoke-Fixture @{} '^The official release manifest is not valid JSON\. Setup was not run\.$'
    Assert-True ($fixture.Transport.Requested.Count -eq 1) 'Malformed metadata fetched the setup payload'
}
New-Fixture; [void](Add-Response ([byte[]]@(0xff))); Invoke-Fixture @{} 'translate|decode|Unable'

# Transport matrix: absolute/relative redirects, host and scheme restrictions,
# lying/omitted lengths, cancellation, bounded reads, timeouts and disposal.
$uri = 'https://github.com/isaiahpettingill/MusicMachine/releases/download/v1.2.3/release.json'
foreach ($location in @('https://release-assets.githubusercontent.com/release/asset?signature=fixture', '/isaiahpettingill/MusicMachine/releases/download/v1.2.3/release.json')) {
    New-Fixture; [void](Add-Response @() 302 $location); [void](Add-Response ([byte[]]@(65)))
    Expect-Result { $result = Receive-MusicMachineRelease $uri 1 5; Assert-True ($result -ceq 'A') 'Redirect lost response' }
    Assert-True ($fixture.Transport.Requested.Count -eq 2 -and $fixture.Streams[0].Closed) 'Redirect was not followed/disposed'
}
foreach ($location in @('http://github.com/file', 'https://evil.invalid/file', 'https://github.com.evil.invalid/file', 'https://user@github.com/file', 'https://github.com:444/file', 'https://github.com/file#fragment', '//evil.invalid/file', 'file:///C:/file')) {
    New-Fixture; [void](Add-Response @() 302 $location)
    Expect-Result { Receive-MusicMachineRelease $uri 10 5 } 'allowed HTTPS'
    Assert-True ($fixture.Transport.Requested.Count -eq 1) 'Unsafe redirect was requested'
}
New-Fixture
for ($i = 0; $i -lt 7; $i++) { [void](Add-Response @() 302 '/loop') }
Expect-Result { Receive-MusicMachineRelease $uri 10 5 } 'redirect limit'
Assert-True ($fixture.Transport.Requested.Count -eq 6) 'Redirect loop exceeded bound'
New-Fixture; $fixture.Transport.HeaderDelayMilliseconds = 400
for ($i = 0; $i -lt 5; $i++) { [void](Add-Response @() 302 '/delayed') }
Expect-Result { Receive-MusicMachineRelease $uri 10 1 } 'cancel'
Assert-True ($fixture.Transport.Requested.Count -le 3) 'Redirects reset the overall deadline'
foreach ($status in @(301, 303, 307, 308)) {
    New-Fixture; [void](Add-Response @() $status '/redirected'); [void](Add-Response ([byte[]]@(65)))
    Expect-Result { $result = Receive-MusicMachineRelease $uri 1 5; Assert-True ($result -ceq 'A') 'Redirect status lost response' }
}
New-Fixture; [void](Add-Response @() 302); Expect-Result { Receive-MusicMachineRelease $uri 10 5 } 'Location is missing'
New-Fixture; [void](Add-Response @() 404); Expect-Result { Receive-MusicMachineRelease $uri 10 5 } 'HTTP 404'
New-Fixture; [void](Add-Response ([byte[]]@(65)) 200 '' 1000); Expect-Result { Receive-MusicMachineRelease $uri 10 5 } 'byte limit'
Assert-True ($fixture.Streams[0].TotalRead -eq 0) 'Oversized declared response was read'
New-Fixture; [void](Add-Response ([byte[]]@(65)) 200 '' 2); Expect-Result { Receive-MusicMachineRelease $uri 10 5 } 'size mismatch'
New-Fixture; [void](Add-Response ([byte[]]@(65,66)) 200 '' 1); Expect-Result { Receive-MusicMachineRelease $uri 10 5 } 'size mismatch'
New-Fixture; $response = Add-Response ([byte[]]@(65)); $response.Content.Headers.ContentEncoding.Add('gzip')
Expect-Result { Receive-MusicMachineRelease $uri 10 5 } 'Encoded release'
New-Fixture; [void](Add-Response ([byte[]]@(65))); $fixture.Streams[0].CancelRead = $true
Expect-Result { Receive-MusicMachineRelease $uri 10 5 } 'cancel'
Assert-True ($fixture.Streams[0].Closed -and $fixture.Transport.DisposedClients -eq 1) 'Cancellation leaked transport'
foreach ($phase in @('Headers', 'Body', 'BodyIgnoringToken')) {
    New-Fixture; [void](Add-Response ([byte[]]@(65)))
    if ($phase -eq 'Headers') { $fixture.Transport.StallHeaders = $true } else { $fixture.Streams[0].StallRead = $true; $fixture.Streams[0].IgnoreReadCancellation = $phase -eq 'BodyIgnoringToken' }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Expect-Result { Receive-MusicMachineRelease $uri 10 1 } 'cancel'
    Assert-True ($watch.Elapsed.TotalSeconds -lt 5 -and $fixture.Transport.DisposedClients -eq 1) "$phase deadline was not enforced"
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('musicmachine-stream-test-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Fixture; [void](Add-Response ([byte[]]@(65)) 200 '' 100)
    Expect-Result { Receive-MusicMachineRelease $uri 10 5 $temp } 'byte limit'
    Assert-True (-not (Test-Path -LiteralPath $temp)) 'Declared oversized body created a file'
    New-Fixture; [void](Add-Response ([byte[]]@(65,66))); $fixture.Streams[0].ChunkSize = 1; $fixture.Streams[0].CancelAfter = 1
    Expect-Result { Receive-MusicMachineRelease $uri 2 5 $temp } 'cancel'
    Assert-True (-not (Test-Path -LiteralPath $temp)) 'Cancelled download left a partial file'
    Assert-True ($fixture.Streams[0].TotalRead -eq 1) 'Partial-write cancellation was not exercised'
    New-Fixture; [void](Add-Response ([byte[]]@(65)) 200 '' 1)
    Expect-Result { Receive-MusicMachineRelease $uri 2 5 $temp } 'size mismatch'
    Assert-True (-not (Test-Path -LiteralPath $temp)) 'Declared short body created an output'
    New-Fixture; [void](Add-Response ([byte[]]@(65,66))); $fixture.Streams[0].ChunkSize = 1
    Expect-Result { Receive-MusicMachineRelease $uri 2 5 $temp }
    Assert-True ([IO.File]::ReadAllText($temp) -ceq 'AB') 'Valid chunked body was changed'
    New-Fixture; [void](Add-Response ([byte[]]@(65,66)))
    Expect-Result { Receive-MusicMachineRelease $uri 2 5 $temp } 'already exists|exist'
    Assert-True ([IO.File]::ReadAllText($temp) -ceq 'AB') 'Existing output was overwritten or removed'
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
# Verify the production factory settings independently; this opens no connection.
$client = & $realClientFactory
try { Assert-True ($client.Timeout -eq [Threading.Timeout]::InfiniteTimeSpan) 'Per-request timeout replaced the overall deadline' }
finally { $client.Dispose() }
Write-Host "Windows bootstrap: $checks isolated source/transport checks passed (no Windows installer executed)."
