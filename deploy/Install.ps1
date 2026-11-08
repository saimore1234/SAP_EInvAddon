<#
  Generic installer for the E-Invoice & ERPNext add-on (same package for every customer).
  Run on the SAP Business One client PC (elevated). It copies the files and prints the registration steps.
  It does NOT touch SAP data, does NOT create any configuration and stores no credentials.
#>
param([string]$Target = "$env:ProgramFiles\EInvGstAddon")
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "app"
if (-not (Test-Path "$src\EInvGstAddon.exe")) { throw "Run this script from the unpacked package (app\EInvGstAddon.exe not found)." }

$net = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
if ($net -lt 528040) { throw ".NET Framework 4.8 or later is required (found release $net)." }
if (-not (Test-Path "$env:ProgramFiles\SAP\SAP Business One\SAPbouiCOM.exe")) { Write-Warning "SAP Business One 10.0 (64-bit) client not found in the default location." }

New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item "$src\*" $Target -Recurse -Force
Write-Host "Installed to $Target"
Write-Host @"

Next steps (once per SAP client / company):
 1. Register $Target\EInvGstAddon.exe in SAP Business One: Administration > Add-Ons > Add-On Administration
    (SAP's Add-On Registration Data Creator makes the .ard file; run as a SAP user with add-on permissions).
 2. Log in to the SAP company, open  Modules > E-Invoice & ERPNext Integration > ERPNext Connection Settings.
 3. Enter ERPNext URL, API key / secret and the ERPNext company, click 'Create SAP UDF/UDT', 'Create ERPNext Fields',
    Test Connection, Test Authentication, Save Configuration. Log out and in again so SAP loads the new fields.
 4. Configure Tax & Account Mapping. GST Configuration can be completed later; invoice synchronisation works without it.
See docs\DEPLOYMENT.md and docs\USER_MANUAL.md.
"@
