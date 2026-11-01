$ErrorActionPreference = 'Stop'
try {
    $root = [IO.Path]::GetFullPath($PSScriptRoot)
    $relative = 'CoopStory'
    $pointer = Join-Path $root 'current-package.txt'
    if (Test-Path -LiteralPath $pointer) {
        $relative = [IO.File]::ReadAllText($pointer).Trim()
        if ($relative -notmatch '^\.updates[\\/][0-9a-f]{32}[\\/]CoopStory$') {
            throw 'Invalid update pointer. Remove current-package.txt to restore the original version.'
        }
    }
    $package = [IO.Path]::GetFullPath((Join-Path $root $relative))
    $cursor = [IO.DirectoryInfo]$package
    while ($null -ne $cursor) {
        if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Launcher folder is a link.' }
        $cursor = $cursor.Parent
    }
    $exe = Join-Path $package 'CoopStory.Launcher.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Launcher missing. Extract the complete package.' }
    Start-Process -FilePath $exe -WorkingDirectory $package
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'RDR2 Coop Story')
    exit 1
}
