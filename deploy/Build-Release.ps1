<#
  Builds the add-on (Release, x64), runs the automated tests and stages a customer-neutral package:
    dist\EInvGstAddon-<version>\   (exe + dlls + Install.ps1 + docs + frappe_app)
    dist\EInvGstAddon-<version>.zip
  The same package is used for every customer; nothing in it is customer-specific and no credentials are included.
#>
param([string]$Version = "1.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { $msbuild = (& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1) }
if (-not $msbuild) { throw "Visual Studio MSBuild not found (the SAP COM interops need the .NET Framework MSBuild)." }

Push-Location $root
try {
    & $msbuild EInvGstAddon.sln -restore -p:Configuration=Release -v:q -nologo "-clp:ErrorsOnly;Summary"
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    dotnet test src\EInvGstAddon.Tests\EInvGstAddon.Tests.csproj -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed - package not created" }

    $out = Join-Path $root "dist\EInvGstAddon-$Version"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    New-Item -ItemType Directory -Force -Path "$out\app","$out\docs" | Out-Null
    Copy-Item "src\EInvGstAddon\bin\Release\net48\*" "$out\app" -Recurse -Exclude "*.pdb","config.json"
    Copy-Item "docs\*.md" "$out\docs"
    Copy-Item "README.md" $out
    Copy-Item "frappe_app" "$out\frappe_app" -Recurse
    Copy-Item "deploy\Install.ps1" $out
    $zip = "$out.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$out\*" -DestinationPath $zip
    Write-Host "Package: $zip"
} finally { Pop-Location }
