<#
  Sets up a new IIS site for SchoolFlow. Run in an ADMINISTRATOR PowerShell from the folder
  this script is in (the one that also contains the "site" folder).

    .\Install-SchoolFlow.ps1 -HostName forms.example.org                  # http only; add the https binding yourself
    .\Install-SchoolFlow.ps1 -HostName forms.example.org -CertThumbprint 'AB12...'   # also adds the https binding

  It creates the folder, app pool and site, and sets folder permissions. It does NOT touch any
  existing site, and stops if a site or app pool named SchoolFlow already exists.
  Needs: IIS installed, and the .NET 8 Hosting Bundle installed after IIS.
#>
param(
  [Parameter(Mandatory = $true)][string]$HostName,
  [string]$SitePath = 'C:\inetpub\SchoolFlow',
  [string]$CertThumbprint = ''
)
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

if (-not (Test-Path "$PSScriptRoot\site\SchoolFlow.Web.exe")) { throw "Run this from the unzipped folder: 'site\SchoolFlow.Web.exe' was not found next to the script." }
if (Test-Path 'IIS:\AppPools\SchoolFlow') { throw 'An app pool named SchoolFlow already exists. For an upgrade, follow section 8 of DEPLOY-IIS.md instead.' }
if (Get-Website -Name 'SchoolFlow') { throw 'A site named SchoolFlow already exists. For an upgrade, follow section 8 of DEPLOY-IIS.md instead.' }
if (-not (Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.AspNetCore.App" -Filter '8.*' -ErrorAction SilentlyContinue)) {
  Write-Warning 'The .NET 8 Hosting Bundle does not appear to be installed. Install it (https://dotnet.microsoft.com/download/dotnet/8.0, "Hosting Bundle"), run iisreset, then run this again.'
  return
}

# Files
New-Item -ItemType Directory -Force -Path $SitePath | Out-Null
Copy-Item "$PSScriptRoot\site\*" $SitePath -Recurse -Force
New-Item -ItemType Directory -Force -Path "$SitePath\App_Data" | Out-Null
if ((Test-Path "$PSScriptRoot\appsettings.Production.json") -and -not (Test-Path "$SitePath\appsettings.Production.json")) {
  Copy-Item "$PSScriptRoot\appsettings.Production.json" $SitePath
}

# App pool: no managed runtime (ASP.NET Core runs in-process), kept awake for background work
New-WebAppPool -Name SchoolFlow | Out-Null
Set-ItemProperty IIS:\AppPools\SchoolFlow -Name managedRuntimeVersion -Value ''
Set-ItemProperty IIS:\AppPools\SchoolFlow -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty IIS:\AppPools\SchoolFlow -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
# Restart the pool once a night at 3 a.m. instead of every 29 hours (which lands during the school day)
Set-ItemProperty IIS:\AppPools\SchoolFlow -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Clear-ItemProperty IIS:\AppPools\SchoolFlow -Name recycling.periodicRestart.schedule
New-ItemProperty IIS:\AppPools\SchoolFlow -Name recycling.periodicRestart.schedule -Value @{ value = '03:00:00' } | Out-Null

# Site
New-Website -Name SchoolFlow -Port 80 -HostHeader $HostName -PhysicalPath $SitePath -ApplicationPool SchoolFlow | Out-Null
# Start the app as soon as the pool starts (after the nightly restart), not on the first visitor's request
Set-ItemProperty IIS:\Sites\SchoolFlow -Name applicationDefaults.preloadEnabled -Value $true
if ($CertThumbprint) {
  New-WebBinding -Name SchoolFlow -Protocol https -Port 443 -HostHeader $HostName -SslFlags 1
  (Get-WebBinding -Name SchoolFlow -Protocol https).AddSslCertificate($CertThumbprint.Replace(' ', ''), 'My')
}

# Permissions: read the site, write only App_Data (sign-in keys and uploaded files)
icacls $SitePath /grant 'IIS AppPool\SchoolFlow:(OI)(CI)RX' | Out-Null
icacls "$SitePath\App_Data" /grant 'IIS AppPool\SchoolFlow:(OI)(CI)M' | Out-Null
if (Test-Path "$SitePath\appsettings.Production.json") {
  icacls "$SitePath\appsettings.Production.json" /inheritance:r /grant 'Administrators:F' 'SYSTEM:F' 'IIS AppPool\SchoolFlow:R' | Out-Null
}

Write-Host ''
Write-Host "Site created at $SitePath for $HostName." -ForegroundColor Green
if (-not $CertThumbprint) { Write-Host 'Next: in IIS Manager add an https binding (port 443, this host name, your certificate). Sign-in needs https.' }
Write-Host "Then: fill in $SitePath\appsettings.Production.json, restart the site, and open https://$HostName/healthz (it should say ok)."
