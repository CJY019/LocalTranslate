param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$Version,
    [string]$FrontendExe = '.\LocalTranslate.exe',
    [string]$OutputDirectory = '.\dist',
    [string]$CacheDirectory,
    [switch]$Force,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$root = Split-Path $PSScriptRoot -Parent
$binary = (Resolve-Path -LiteralPath $FrontendExe).ProviderPath
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $output '.cache' }
New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
$cache = (Resolve-Path -LiteralPath $CacheDirectory).ProviderPath
$work = Join-Path $output ('.build-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $work 'app'
$source = Join-Path $work 'source'
$python = Join-Path $app 'runtime\python'
$llama = Join-Path $app 'runtime\llama'
$detector = Join-Path $app 'runtime\language-detection'
$licenses = Join-Path $app 'runtime\licenses'
$sourceApp = Join-Path $source 'LocalTranslate'
$binaryZip = Join-Path $output ("LocalTranslate-$Version-windows-x64.zip")
$sourceZip = Join-Path $output ("LocalTranslate-$Version-corresponding-source.zip")
$sums = Join-Path $output ("LocalTranslate-$Version-SHA256SUMS.txt")

foreach ($artifact in @($binaryZip, $sourceZip, $sums)) {
    if ((Test-Path -LiteralPath $artifact) -and -not $Force) {
        throw "Output already exists: $artifact. Use -Force to replace it."
    }
}
foreach ($directory in @($app, $sourceApp, $python, $llama, $detector, $licenses)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
$oldCompilerTemp = $env:TMPDIR
$env:TEMP = $work
$env:TMP = $work
$env:TMPDIR = $work

function Get-Dependency($item) {
    $destination = Join-Path $cache $item.file
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $item.sha256) {
            return $destination
        }
    }
    $partial = $destination + '.partial'
    Write-Host "Downloading $($item.name)"
    $failure = $null
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            Invoke-WebRequest -Uri $item.url -OutFile $partial -UseBasicParsing
            $actual = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash
            if ($actual -ne $item.sha256) {
                throw "SHA-256 mismatch for $($item.file): $actual"
            }
            Move-Item -LiteralPath $partial -Destination $destination -Force
            return $destination
        } catch {
            $failure = $_
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        }
    }
    throw $failure
}

function Extract-Zip([string]$archive, [string]$destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $destination)
}

function Copy-Range([string]$file, [string]$target, [long]$offset, [long]$length) {
    $inputStream = [IO.File]::OpenRead($file)
    try {
        $outputStream = [IO.File]::Create($target)
        try {
            [void]$inputStream.Seek($offset, [IO.SeekOrigin]::Begin)
            $buffer = New-Object byte[] (1024 * 1024)
            $remaining = $length
            while ($remaining -gt 0) {
                $requested = [int][Math]::Min($buffer.Length, $remaining)
                $read = $inputStream.Read($buffer, 0, $requested)
                if ($read -eq 0) { throw "Unexpected end of $file" }
                $outputStream.Write($buffer, 0, $read)
                $remaining -= $read
            }
        } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
}

function Expand-Cab([string]$cab, [string]$directory) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    & "$env:SystemRoot\System32\expand.exe" '-F:*' $cab $directory | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot extract $cab" }
}

function Copy-Tree([string]$from, [string]$to, [string[]]$extensions) {
    if (-not (Test-Path -LiteralPath $from -PathType Container)) { return }
    $prefix = [IO.Path]::GetFullPath($from).TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $from -Recurse -File) {
        if ($extensions -notcontains $file.Extension.ToLowerInvariant()) { continue }
        $relative = $file.FullName.Substring($prefix.Length)
        if ($relative -match '(^|\\)(__pycache__|tests?|fixtures)(\\|$)' -or
            $file.Name -match '^(test_|.*\.test\.)') { continue }
        $target = Join-Path $to $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function New-DeterministicZip([string]$directory, [string]$destination) {
    $partial = $destination + '.partial'
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    $stream = [IO.File]::Open($partial, [IO.FileMode]::CreateNew)
    try {
        $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $prefix = [IO.Path]::GetFullPath($directory).TrimEnd('\') + '\'
            $files = Get-ChildItem -LiteralPath $directory -Recurse -File | Sort-Object FullName
            foreach ($file in $files) {
                $name = $file.FullName.Substring($prefix.Length).Replace('\', '/')
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = New-Object DateTimeOffset(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entryStream = $entry.Open()
                try {
                    $fileStream = $file.OpenRead()
                    try { $fileStream.CopyTo($entryStream) } finally { $fileStream.Dispose() }
                } finally { $entryStream.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    Move-Item -LiteralPath $partial -Destination $destination -Force
}

try {
    $items = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') | ConvertFrom-Json
    $downloads = @{}
    foreach ($item in $items) { $downloads[$item.kind + '/' + $item.file] = Get-Dependency $item }

    $pythonDependency = $items | Where-Object { $_.kind -eq 'python' }
    Extract-Zip $downloads['python/' + $pythonDependency.file] $python
    # Do not load a developer's user site-packages on machines that already have Python installed.
    [IO.File]::WriteAllLines((Join-Path $python 'python313._pth'), @('python313.zip', '.', 'Lib\site-packages'))
    $sitePackages = Join-Path $python 'Lib\site-packages'
    foreach ($item in ($items | Where-Object { $_.kind -eq 'wheel' })) {
        Extract-Zip $downloads['wheel/' + $item.file] $sitePackages
    }
    Extract-Zip $downloads['detector-wheel/fasttext_predict-0.9.2.3-cp313-cp313-win_amd64.whl'] $detector
    Copy-Item -LiteralPath $downloads['detector-model/lid.176.ftz'] -Destination $detector

    $llamaUnpacked = Join-Path $work 'llama'
    Extract-Zip $downloads['llama/llama-b12633-bin-win-cpu-x64.zip'] $llamaUnpacked
    $llamaNames = @('ggml-base.dll', 'ggml.dll', 'libomp.dll', 'llama-common.dll',
        'llama-server-impl.dll', 'llama-server.exe', 'llama.dll', 'mtmd.dll', 'LICENSE-LLVM-OpenMP')
    foreach ($name in $llamaNames) {
        Copy-Item -LiteralPath (Join-Path $llamaUnpacked $name) -Destination $llama
    }
    Get-ChildItem -LiteralPath $llamaUnpacked -Filter 'ggml-cpu-*.dll' -File |
        Copy-Item -Destination $llama
    Copy-Item -LiteralPath $downloads['license/llama-MIT.txt'] -Destination $licenses
    Copy-Item -LiteralPath $downloads['license/CC-BY-SA-3.0.txt'] -Destination $licenses

    # The pinned Microsoft bundle contains two signed CABs at these fixed offsets.
    # Its hash is checked above, and the three extracted DLLs are checked below.
    $redistributable = $downloads['vcredist/VC_redist.x64.exe']
    $uxCab = Join-Path $work 'vcredist-ux.cab'
    $payloadCab = Join-Path $work 'vcredist-payload.cab'
    Copy-Range $redistributable $uxCab 479744 195988
    Copy-Range $redistributable $payloadCab 686152 24939223
    $ux = Join-Path $work 'vcredist-ux'
    $payload = Join-Path $work 'vcredist-payload'
    Expand-Cab $uxCab $ux
    Expand-Cab $payloadCab $payload
    $runtimeCab = Join-Path $work 'vcredist-runtime.cab'
    Copy-Range (Join-Path $payload 'a12') $runtimeCab 0 977468
    $runtimeFiles = Join-Path $work 'vcredist-runtime'
    Expand-Cab $runtimeCab $runtimeFiles
    $dlls = @{
        'msvcp140.dll' = '0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9'
        'vcruntime140.dll' = 'd5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066'
        'vcruntime140_1.dll' = '1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f'
    }
    foreach ($name in $dlls.Keys) {
        $file = Join-Path $runtimeFiles ($name + '_amd64')
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $dlls[$name]) {
            throw "Unexpected Microsoft runtime file: $name"
        }
        Copy-Item -LiteralPath $file -Destination (Join-Path $llama $name)
        $pythonLibrary = Join-Path $python $name
        if (-not (Test-Path -LiteralPath $pythonLibrary)) { Copy-Item -LiteralPath $file -Destination $pythonLibrary }
    }
    Copy-Item -LiteralPath (Join-Path $ux 'u4') -Destination (Join-Path $licenses 'Microsoft-VC-Runtime.rtf')

    Copy-Item -LiteralPath $binary -Destination (Join-Path $app 'LocalTranslate.exe')
    Copy-Tree (Join-Path $root 'backend') (Join-Path $app 'backend') @('.py', '.ps1')
    Copy-Tree (Join-Path $root 'langpacks\builtin') (Join-Path $app 'langpacks\builtin') @('.json')
    Copy-Item -LiteralPath (Join-Path $root 'langpacks\manifest.json') -Destination (Join-Path $app 'langpacks')
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $app
    Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $app
    Copy-Tree (Join-Path $root 'docs') (Join-Path $app 'docs') @('.md')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $app
    New-Item -ItemType Directory -Path (Join-Path $app 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Destination (Join-Path $app 'scripts')
    [IO.File]::WriteAllText((Join-Path $app 'INSTALL.md'), '# Install LocalTranslate' + "`r`n`r`n" +
        'Open LocalTranslate.exe to begin, or run install.cmd to add a desktop shortcut and start the app.' + "`r`n`r`n" +
        '[Installation guide](docs/install.md)' + "`r`n", [Text.Encoding]::ASCII)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $app
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.cmd') -Destination $app

    foreach ($part in @('frontend', 'backend', 'benchmarks', 'langpacks', 'tools', 'scripts', 'docs')) {
        $extensions = switch ($part) {
            'frontend' { @('.cs', '.xaml', '.ps1', '.manifest', '.ico', '.png') }
            'backend' { @('.py', '.ps1', '.json') }
            'benchmarks' { @('.py') }
            'langpacks' { @('.json') }
            'tools' { @('.py', '.ps1') }
            'scripts' { @('.py', '.ps1', '.cmd', '.json') }
            'docs' { @('.md') }
        }
        Copy-Tree (Join-Path $root $part) (Join-Path $sourceApp $part) $extensions
    }
    $testSource = Join-Path $root 'tests\test_model_management.py'
    New-Item -ItemType Directory -Path (Join-Path $sourceApp 'tests') -Force | Out-Null
    Copy-Item -LiteralPath $testSource -Destination (Join-Path $sourceApp 'tests')
    foreach ($name in @('LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md', '.gitignore', '.gitattributes')) {
        $file = Join-Path $root $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { Copy-Item -LiteralPath $file -Destination $sourceApp }
    }
    $upstream = Join-Path $source 'upstream'
    New-Item -ItemType Directory -Path $upstream -Force | Out-Null
    foreach ($item in ($items | Where-Object { $_.kind -eq 'source' })) {
        Copy-Item -LiteralPath $downloads['source/' + $item.file] -Destination $upstream
    }

    $smoke = Join-Path $work 'verify-runtime.py'
    [IO.File]::WriteAllText($smoke, @'
import sys

sys.path.insert(0, sys.argv[1])
import docx, fitz, lxml.etree, markdown_it, languages

assert fitz.VersionBind == "1.27.2.3"
assert languages.detect_language("The train arrives at the station tomorrow morning.") == "en"
print("Python, PDF, DOCX, Markdown, XML and fastText imports OK")
'@, [Text.Encoding]::ASCII)
    & (Join-Path $python 'python.exe') -I -B $smoke (Join-Path $app 'backend')
    if ($LASTEXITCODE -ne 0) { throw 'Bundled Python dependency verification failed.' }
    & (Join-Path $llama 'llama-server.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'Bundled llama.cpp verification failed.' }

    Write-Host 'Creating application archive'
    New-DeterministicZip $app $binaryZip
    Write-Host 'Creating corresponding-source archive'
    New-DeterministicZip $source $sourceZip
    $lines = foreach ($file in @($binaryZip, $sourceZip)) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
    }
    [IO.File]::WriteAllLines($sums, [string[]]$lines, [Text.Encoding]::ASCII)
    Write-Host "Created $binaryZip"
    Write-Host "Created $sourceZip"
    Write-Host "Created $sums"
} finally {
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    $env:TMPDIR = $oldCompilerTemp
    if (-not $KeepWork -and (Test-Path -LiteralPath $work)) {
        $absoluteWork = [IO.Path]::GetFullPath($work)
        $outputPrefix = [IO.Path]::GetFullPath($output).TrimEnd('\') + '\'
        if (-not $absoluteWork.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove work directory outside output: $absoluteWork"
        }
        Remove-Item -LiteralPath $absoluteWork -Recurse -Force
    }
}
