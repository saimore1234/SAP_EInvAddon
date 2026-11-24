<#
  Builds the add-on as 32-bit (x86) for an older / 32-bit SAP Business One client (e.g. 9.3) and packages it for Extension Manager.

  RECOMMENDED (build on YOUR pc, nothing to install on the client's server):
      1. Copy SAPbouiCOM.dll and SAPbobsCOM.dll from the client's SAP Business One installation into  C:\EInvGstAddon\lib93
      2. powershell -ExecutionPolicy Bypass -File deploy\Build-Client93.ps1 -LibDir C:\EInvGstAddon\lib93 -Version 2.1.0

  ALTERNATIVE (build ON the client PC, SAP client + DI API installed there):
      powershell -ExecutionPolicy Bypass -File deploy\Build-Client93.ps1 -Version 2.1.0

  Needs: Visual Studio / Build Tools (MSBuild, .NET Framework 4.8 targeting pack), Windows SDK TlbImp (mode with -LibDir),
         SAP Business One SDK "ExtensionPackage" tool (package step only).
#>
param(
    [string]$Version = "2.1.0",
    [int]$ApiMajor = 0,          # registered-type-library mode only; 0 = detect
    [int]$ApiMinor = -1,
    [string]$LibDir = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- 1. which SAP API does the build use? --------------------------------------------------------------------------------------------------
$interopDir = ""
if ($LibDir) {
    # Mode A: the client's SAP files were copied here; Interop assemblies are generated from THEIR type libraries (so they match their version).
    $tlb = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft SDKs\Windows\*\bin\NETFX* Tools\TlbImp.exe" -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notlike "*\x64\*" } | Select-Object -Last 1
    if (-not $tlb) { throw "TlbImp.exe (Windows SDK / .NET Framework tools) was not found." }
    $interopDir = Join-Path $root "build\interop93"
    if (Test-Path $interopDir) { Remove-Item $interopDir -Recurse -Force }
    New-Item -ItemType Directory -Force $interopDir | Out-Null
    # UI API is the file SAPbouiCOM.exe; the DI API is SAPbobsCOM<version>.dll (SAPbobsCOM90.dll = 9.x, SAPbobsCOM100.dll = 10.0). Any of those names is accepted.
    $pick = @{
        "SAPbouiCOM" = (Get-ChildItem $LibDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^SAPbouiCOM\.(exe|dll)$' } | Select-Object -First 1)
        "SAPbobsCOM" = (Get-ChildItem $LibDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^SAPbobsCOM[0-9]*\.dll$' } | Select-Object -First 1)
    }
    foreach ($n in "SAPbouiCOM", "SAPbobsCOM") {
        $src = $pick[$n]
        if (-not $src) { throw "No file for $n found in $LibDir. Copy SAPbouiCOM.exe (UI API) and SAPbobsCOM90.dll (DI API) from the client's SAP Business One installation." }
        Write-Host "Using $($src.FullName)"
        foreach ($machine in "X86", "Agnostic", "X64") {        # the client's 32-bit libraries import as X86; the others are only for a library of another bitness
            & $tlb.FullName $src.FullName "/out:$interopDir\Interop.$n.dll" "/machine:$machine" "/namespace:$n" | Out-Host
            if ($LASTEXITCODE -eq 0 -and (Test-Path "$interopDir\Interop.$n.dll")) { break }
        }
        if (-not (Test-Path "$interopDir\Interop.$n.dll")) { throw "TlbImp failed for $($src.Name)" }
    }
    Write-Host "Interop assemblies generated from the client's SAP libraries: $interopDir" -ForegroundColor Cyan
}
else {
    # Mode B (run ON the client PC): use the SAP type libraries registered there.
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
        if (-not $v) { throw "The SAP UI API type library is not registered on this PC. Use -LibDir with the client's SAPbouiCOM.dll / SAPbobsCOM.dll, or install the SAP Business One client here." }
        $parts = $v.Split(".")                                           # the registry stores type-library versions in HEXADECIMAL (a.0 = 10.0, 9.1e = 9.30)
        $ApiMajor = [Convert]::ToInt32($parts[0], 16); $ApiMinor = [Convert]::ToInt32($parts[1], 16)
    }
    Write-Host "Building against SAP API type library $ApiMajor.$ApiMinor (x86)" -ForegroundColor Cyan
}

# ---- 2. MSBuild ---------------------------------------------------------------------------------------------------------------------------
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) { $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1 }
if (-not $msbuild) { throw "MSBuild was not found. Install 'Build Tools for Visual Studio' with the '.NET desktop build tools' workload." }

$out = Join-Path $root "build\x86"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
[string[]]$sapArgs = if ($interopDir) { @("-p:SapInteropDir=$interopDir") } else { @("-p:SapApiMajor=$ApiMajor", "-p:SapApiMinor=$ApiMinor") }
& $msbuild "src\EInvGstAddon\EInvGstAddon.csproj" -restore -p:Configuration=Release -p:Platform=AnyCPU -p:PlatformTarget=x86 @sapArgs "-p:OutDir=$out\" -v:q -nologo "-clp:ErrorsOnly;Summary"
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
$files = "EInvGstAddon.exe", "EInvGstAddon.exe.config", "EInvGstAddon.Common.dll", "EInvGstAddon.Core.dll", "EInvGstAddon.Database.dll", "EInvGstAddon.ERPNext.dll", "EInvGstAddon.UI.dll", "Interop.SAPbobsCOM.dll", "Interop.SAPbouiCOM.dll", "Newtonsoft.Json.dll", "QRCoder.dll"
foreach ($f in $files) { Copy-Item (Join-Path $out $f) "$pkg\X86Client\" }
Copy-Item "deploy\EInvGstAddon.x86.ard" "$pkg\template.ard"
New-Item -ItemType Directory -Force "dist" | Out-Null
$zip = Join-Path $root "dist\EInvGstAddon-$Version-x86.zip"
$p = Start-Process -FilePath $tool.FullName -ArgumentList @("/86:$pkg\X86Client\EInvGstAddon.exe", "/s:$pkg\template.ard", "/p:$zip", "/v:$Version", "/ex:.pdb") -PassThru -WindowStyle Hidden -RedirectStandardOutput "$pkg\out.txt" -RedirectStandardError "$pkg\err.txt"
if (-not $p.WaitForExit(90000)) { $p.Kill() }      # the tool waits for a key press at the end; the package is already written by then
if (-not (Test-Path $zip)) { Get-Content "$pkg\out.txt", "$pkg\err.txt" -ErrorAction SilentlyContinue; throw "The package was not created." }
Write-Host "Extension Manager package: $zip" -ForegroundColor Green
