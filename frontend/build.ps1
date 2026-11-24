param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$locator = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$compiler = if (Test-Path -LiteralPath $locator) {
    & $locator -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\Roslyn\csc.exe' | Select-Object -First 1
}
if (!$compiler -or !(Test-Path -LiteralPath $compiler)) {
    throw 'Install Visual Studio 2022 or later / Build Tools with the .NET desktop build tools workload (MSBuild and Roslyn).'
}
$framework = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319'
$refs = @('mscorlib','System','System.Core','System.Net.Http','System.Web.Extensions','System.Xaml','System.Xml','System.Xml.Linq','System.Runtime.Serialization','System.Windows.Forms','System.Drawing','System.Runtime','System.Runtime.WindowsRuntime')
$wpf = @('WindowsBase','PresentationCore','PresentationFramework','UIAutomationClient','UIAutomationTypes')
$projectRoot = Split-Path $PSScriptRoot
if (!$OutputPath) { $OutputPath = Join-Path $projectRoot 'LocalTranslate.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$arguments = @('/nologo','/noconfig','/nostdlib+','/optimize+','/deterministic+','/target:winexe','/platform:x64','/codepage:65001',"/out:$OutputPath","/win32manifest:$PSScriptRoot/app.manifest","/win32icon:$PSScriptRoot/assets/translate.ico")
foreach ($assembly in $refs) { $arguments += "/reference:$framework/$assembly.dll" }
foreach ($assembly in $wpf) { $arguments += "/reference:$framework/WPF/$assembly.dll" }
$metadataRoot = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\UnionMetadata'
$winMetadata = Get-ChildItem -Path "$metadataRoot\10.*\Windows.winmd" -File | Sort-Object { [version]$_.Directory.Name } -Descending | Select-Object -First 1
if (!$winMetadata) { throw "Windows SDK Windows.winmd not found in $metadataRoot" }
$arguments += "/reference:$($winMetadata.FullName)"
foreach ($asset in @('Main.xaml','Styles.xaml','Popup.xaml')) { $arguments += "/resource:$PSScriptRoot/$asset,$asset" }
$arguments += "/resource:$PSScriptRoot/assets/translate.ico,translate.ico"
foreach ($locale in @('zh-CN','en-US')) { $arguments += "/resource:$projectRoot/langpacks/builtin/$locale.json,ui.$locale.json" }
$arguments += "/resource:$projectRoot/langpacks/manifest.json,ui.manifest.json"
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object { $_.FullName })
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    $buildTemp = Join-Path (Split-Path $PSScriptRoot) 'temp'
    [void](New-Item -ItemType Directory -Path $buildTemp -Force)
    [void](New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force)
    $env:TEMP = $buildTemp
    $env:TMP = $buildTemp
    & $compiler @arguments @sources
    if ($LASTEXITCODE -ne 0) { throw "C# compilation failed ($LASTEXITCODE)" }
} finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}
