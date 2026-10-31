<#
.SYNOPSIS
    Builds the Microsoft Store version of LocalBell: an MSIX package.

.DESCRIPTION
    The same app as the installer of scripts\build.ps1 (which this script does not change), packaged for the Store, as
    NowPlaying's: it runs with the package's identity, starts with Windows through its startup task, opens the firewall
    through the rules of its manifest, and leaves its updates to the Store (Services\AppPackage.cs). Output in
    dist\store:
      layout\                         the content of the package: the app, self-contained (.NET 10 inside: a package
                                      cannot install the runtime), the manifest (store\AppxManifest.xml) and the logos
                                      (store\Assets)
      LocalBell_<version>.0_x64.msix  the package, not signed: the Store signs it when it publishes it

    Prerequisites: see "Development" in the README (the Windows SDK: makeappx, makepri).

    To try the package on this PC (Developer Mode, no other LocalBell running):
      Add-AppxPackage -Register dist\store\layout\AppxManifest.xml
    and remove it after (Get-AppxPackage QuentinPerou.LocalBell | Remove-AppxPackage) before the Store installs its own.

.PARAMETER SkipTests
    Do not run the unit tests.

.EXAMPLE
    .\scripts\build-store.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'dist\store'
$layout = Join-Path $out 'layout'

function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Invoke-Native([scriptblock]$Command, [string]$What) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

# The latest Windows SDK tool, or the one on the PATH (a Developer prompt).
function Find-SdkTool([string]$Name) {
    $tool = @(
        (Get-Command $Name -ErrorAction SilentlyContinue).Source
        Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\*\x64\$Name.exe") -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -ExpandProperty FullName
    ) | Where-Object { $_ } | Select-Object -First 1
    if (-not $tool) { throw "Tool not found: $Name (Windows SDK). See the prerequisites in the README." }
    return $tool
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
# The Store takes four numbers, the last one at 0, and no pre-release: betas stay on GitHub.
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version ${version}: the Store version needs a final version (x.y.z)." }
$packageVersion = "$version.0"
Write-Host "LocalBell $version (Store package $packageVersion)" -ForegroundColor Green

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Tool not found: dotnet. See the prerequisites in the README.' }
$makeappx = Find-SdkTool 'makeappx'
$makepri = Find-SdkTool 'makepri'

# Start from an empty dist\store folder (the rest of dist is build.ps1's).
if ([IO.Directory]::Exists($out)) { [IO.Directory]::Delete($out, $true) }
New-Item -ItemType Directory -Force $layout | Out-Null

if (-not $SkipTests) {
    Write-Step 'Tests'
    Invoke-Native { dotnet test (Join-Path $root 'tests\LocalBell.Core.Tests') -c Release --nologo } 'The tests'
}

Write-Step 'Application (.NET, self-contained)'
Invoke-Native {
    dotnet publish (Join-Path $root 'src\LocalBell.App') -c Release -r win-x64 --self-contained true `
        -p:DebugType=none -o $layout --nologo
} 'Building the application'

Write-Step 'Manifest and logos'
$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
Copy-Item (Join-Path $root 'store\Assets\*') $assets
$manifest = [IO.File]::ReadAllText((Join-Path $root 'store\AppxManifest.xml'))
$manifest = $manifest -replace '(<Identity[^>]*?Version=")0\.0\.0\.0(")', "`${1}$packageVersion`${2}"
if ($manifest -notmatch [regex]::Escape("Version=`"$packageVersion`"")) { throw 'Version not written in the manifest.' }
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding $false))

Write-Step 'Resources index (makepri)'
# The index of the logos only, so that Windows finds the size it needs: made from a copy of the Assets folder, not
# from the whole layout (the app's files are not resources).
$pri = Join-Path $out 'pri'
$priRoot = Join-Path $pri 'root'
$priConfig = Join-Path $pri 'priconfig.xml'
New-Item -ItemType Directory -Force $priRoot | Out-Null
Copy-Item $assets (Join-Path $priRoot 'Assets') -Recurse
Invoke-Native { & $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null } 'makepri createconfig'
# One index for every scale: by default makepri puts each scale in a file of its own, meant for a bundle.
$config = [xml](Get-Content $priConfig)
$config.resources.RemoveChild($config.resources.packaging) | Out-Null
$config.Save($priConfig)
Invoke-Native {
    & $makepri new /pr $priRoot /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') `
        /of (Join-Path $layout 'resources.pri') /o | Out-Null
} 'makepri new'
[IO.Directory]::Delete($pri, $true)

Write-Step 'Package (makeappx)'
$msix = Join-Path $out "LocalBell_${packageVersion}_x64.msix"
Invoke-Native { & $makeappx pack /o /d $layout /p $msix } 'makeappx'

Write-Step 'Done'
Write-Host ("  dist\store\{0}  ({1:N1} MB)" -f (Split-Path -Leaf $msix), ((Get-Item $msix).Length / 1MB))
