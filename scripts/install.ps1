param(
    [string]$InstallDirectory,
    [string]$ShortcutDirectory = [Environment]::GetFolderPath('DesktopDirectory'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if (-not $InstallDirectory) { $InstallDirectory = $source }
$destination = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'LocalTranslate requires 64-bit Windows.'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'LocalTranslate.exe') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $source 'runtime\python\pythonw.exe') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $source 'backend\server.py') -PathType Leaf)) {
    throw 'Extract the complete application ZIP before running this installer.'
}
$driveRoot = [IO.Path]::GetPathRoot($destination).TrimEnd('\')
if ([string]::Equals($destination, $driveRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an installation directory inside a drive, rather than its root.'
}
$sameLocation = [string]::Equals($source, $destination, [StringComparison]::OrdinalIgnoreCase)
if (-not $sameLocation) {
    if ($destination.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $source.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The extracted and installation directories must not be nested.'
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($name in @('LocalTranslate.exe', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'INSTALL.md', 'README.md')) {
        $path = Join-Path $source $name
        if (Test-Path -LiteralPath $path) {
            Copy-Item -LiteralPath $path -Destination (Join-Path $destination $name) -Force
        }
    }
    foreach ($name in @('backend', 'runtime', 'langpacks', 'docs', 'scripts')) {
        $path = Join-Path $source $name
        if (Test-Path -LiteralPath $path -PathType Container) {
            Copy-Item -LiteralPath $path -Destination $destination -Recurse -Force
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ShortcutDirectory)) {
    throw 'The desktop folder could not be located.'
}
New-Item -ItemType Directory -Path $ShortcutDirectory -Force | Out-Null
$shortcutPath = Join-Path $ShortcutDirectory 'LocalTranslate.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $destination 'LocalTranslate.exe'
$shortcut.WorkingDirectory = $destination
$shortcut.Description = 'LocalTranslate'
$shortcut.Save()
Write-Host "Installed to $destination"
Write-Host "Shortcut created at $shortcutPath"
if (-not $NoLaunch) {
    $env:TEMP = Join-Path $destination 'temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
    Start-Process -FilePath $shortcut.TargetPath -WorkingDirectory $destination
}
