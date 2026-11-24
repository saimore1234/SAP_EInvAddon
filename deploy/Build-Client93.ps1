<#
  Builds the add-on as 32-bit (x86) against the SAP Business One API that is INSTALLED ON THIS PC (e.g. 9.3) and packages it for Extension Manager.
  Run it on the client PC, in a normal (not administrator) PowerShell, from the repository folder:
      powershell -ExecutionPolicy Bypass -File deploy\Build-Client93.ps1 -Version 2.1.0
  Needs: SAP Business One client + DI API (32-bit) installed here, Visual Studio Build Tools (MSBuild, .NET Framework 4.8 targeting pack),
         the SAP Business One SDK "ExtensionPackage" tool (only for the package step).
#>
param(
    [string]$Version = "2.1.0",
    [int]$ApiMajor = 0,          # 0 = detect from the registered SAP type libraries
    [int]$ApiMinor = -1
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- 1. which SAP API version is registered on this PC? ------------------------------------------------------------------------------------
$libs = @{ "SAPbobsCOM (DI API)" = "{FC8030BE-F5D2-4B8E-8F92-44228FE30100}"; "SAPbouiCOM (UI API)" = "{6048236A-956D-498D-A6F1-9C81C13AB6E8}" }
$found = @{}
foreach ($k in $libs.Keys) {
    $vers = @()
    foreach ($base in "HKLM:\SOFTWARE\WOW6432Node\Classes\TypeLib", "HKLM:\SOFTWARE\Classes\TypeLib", "HKLM:\SOFTWARE\Classes\WOW6432Node\TypeLib") {
        $p = Join-Path $base $libs[$k]
        if (Test-Path $p) { $vers += Get-ChildItem $p | ForEach-Object { $_.PSChildName } }
    }
    $vers = $vers | Sort-Object -Unique
    Write-Host ("{0} registered versions (hex): {1}" -f $k, ($vers -join ", "))
    $found[$k] = $vers
}
if ($ApiMajor -le 0) {
    $v = $found["SAPbouiCOM (UI API)"] | Select-Object -Last 1
    if (-not $v) { throw "The SAP UI API type library is not registered on this PC. Install the SAP Business One client (and DI API) first, or pass -ApiMajor/-ApiMinor." }
    $parts = $v.Split(".")                                           # the registry stores type-library versions in HEXADECIMAL (a.0 = 10.0, 9.1e = 9.30)
    $ApiMajor = [Convert]::ToInt32($parts[0], 16); $ApiMinor = [Convert]::ToInt32($parts[1], 16)
}
Write-Host "Building against SAP API type library $ApiMajor.$ApiMinor (x86)" -ForegroundColor Cyan

# ---- 2. MSBuild ---------------------------------------------------------------------------------------------------------------------------
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) { $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1 }
if (-not $msbuild) { throw "MSBuild was not found. Install 'Build Tools for Visual Studio' with the '.NET desktop build tools' workload." }

$out = Join-Path $root "build\x86"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $msbuild "src\EInvGstAddon\EInvGstAddon.csproj" -restore -p:Configuration=Release -p:Platform=AnyCPU -p:PlatformTarget=x86 "-p:SapApiMajor=$ApiMajor" "-p:SapApiMinor=$ApiMinor" "-p:OutDir=$out\" -v:q -nologo "-clp:ErrorsOnly;Summary"
if ($LASTEXITCODE -ne 0) { throw "Build failed (see the messages above)." }
$exe = Join-Path $out "EInvGstAddon.exe"
if (-not (Test-Path $exe)) { throw "EInvGstAddon.exe was not produced." }
Write-Host "Built: $exe" -ForegroundColor Green

# ---- 3. Extension Manager package (32-bit) -------------------------------------------------------------------------------------------------
$tool = Get-ChildItem "${env:ProgramFiles(x86)}\SAP\SAP Business One SDK\Tools\ExtensionPackage\ExtensionPackage.exe", "$env:ProgramFiles\SAP\SAP Business One SDK\Tools\ExtensionPackage\ExtensionPackage.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $tool) { Write-Warning "ExtensionPackage.exe (SAP Business One SDK) not found: the build is ready in $out but no Extension Manager package was made."; return }
$pkg = Join-Path $root "build\pkg86"
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force "$pkg\X86Client" | Out-Null
$files = "EInvGstAddon.exe","EInvGstAddon.exe.config","EInvGstAddon.Common.dll","EInvGstAddon.Core.dll","EInvGstAddon.Database.dll","EInvGstAddon.ERPNext.dll","EInvGstAddon.UI.dll","Interop.SAPbobsCOM.dll","Interop.SAPbouiCOM.dll","Newtonsoft.Json.dll","QRCoder.dll"
foreach ($f in $files) { Copy-Item (Join-Path $out $f) "$pkg\X86Client\" }
Copy-Item "deploy\EInvGstAddon.x86.ard" "$pkg\template.ard"
New-Item -ItemType Directory -Force "dist" | Out-Null
$zip = Join-Path $root "dist\EInvGstAddon-$Version-x86.zip"
$p = Start-Process -FilePath $tool.FullName -ArgumentList @("/86:$pkg\X86Client\EInvGstAddon.exe", "/s:$pkg\template.ard", "/p:$zip", "/v:$Version", "/ex:.pdb") -PassThru -WindowStyle Hidden -RedirectStandardOutput "$pkg\out.txt" -RedirectStandardError "$pkg\err.txt"
if (-not $p.WaitForExit(90000)) { $p.Kill() }      # the tool waits for a key press at the end; the package is already written by then
if (-not (Test-Path $zip)) { Get-Content "$pkg\out.txt","$pkg\err.txt" -ErrorAction SilentlyContinue; throw "The package was not created." }
Write-Host "Extension Manager package: $zip" -ForegroundColor Green
