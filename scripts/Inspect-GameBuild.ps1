[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameExe,
    [ValidateSet('Steam','Epic','Rockstar','Unknown')][string]$Store = 'Unknown',
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $GameExe).ProviderPath
$item = Get-Item -LiteralPath $resolved
if ($item.PSIsContainer -or $item.Name -ine 'RDR2.exe') {
    throw 'Select the installed RDR2.exe file.'
}
$policyPath = Join-Path $PSScriptRoot '..\src\CoopStory.Bridge\include\coopstory\bridge\VersionGate.hpp'
$policy = [IO.File]::ReadAllText($policyPath)
$expectedVersion = [regex]::Match($policy, 'kSupportedFileVersion\s*=\s*"([^"]+)"').Groups[1].Value
$expectedHash = [regex]::Match($policy, 'kSupportedExecutableSha256\s*=\s*"([A-Fa-f0-9]{64})"').Groups[1].Value
if (-not $expectedVersion -or -not $expectedHash) { throw 'Cannot read source compatibility policy.' }
$info = $item.VersionInfo
$version = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart,$info.FilePrivatePart
$hash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
$identityMatch = ($version -eq $expectedVersion -and $hash -eq $expectedHash)
$report = [ordered]@{
    schemaVersion = 1
    inspectedUtc = [DateTime]::UtcNow.ToString('o')
    storeDeclaredByUser = $Store
    fileName = $item.Name
    fileVersion = $version
    sha256 = $hash
    expectedFileVersion = $expectedVersion
    expectedSha256 = $expectedHash
    matchesCurrentSourceIdentityGate = $identityMatch
    playableCoopVerified = $false
    note = 'Identity check only. Store label is not verified. No game files were changed. Runtime Story Mode, native bindings and two-PC campaign require separate tests.'
}
$json = $report | ConvertTo-Json -Depth 4
if ($ReportPath) {
    # Explicit path only: never overwrite an executable or an existing report.
    $fullReport = [IO.Path]::GetFullPath($ReportPath)
    if ([IO.Path]::GetExtension($fullReport) -ine '.json') { throw 'Report path must end in .json.' }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [IO.File]::Open($fullReport, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
$json
