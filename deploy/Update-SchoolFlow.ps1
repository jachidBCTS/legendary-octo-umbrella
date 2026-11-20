<#
  Updates the files of an existing SchoolFlow site. Run in an ADMINISTRATOR PowerShell from the
  folder this script is in (the one that also contains the "site" folder).

    .\Update-SchoolFlow.ps1 -SitePath 'X:\wwwroot\SchoolFlow'

  It takes the site offline, copies the new files over the old ones, and brings it back.
  It never touches App_Data (sign-in keys, uploaded files) or appsettings.Production.json, and it
  does not overwrite an existing appsettings.json either (in case settings were put there): the new
  one is saved beside it as appsettings.json.new to compare.
  If the copy fails part-way, the site is left OFFLINE on purpose, so it never runs on a mix of old
  and new files. Fix the cause and run this script again.
  The database is updated by the site itself the next time it starts.
#>
param([Parameter(Mandatory = $true)][string]$SitePath)
$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'site'
if (-not (Test-Path (Join-Path $source 'SchoolFlow.Web.exe'))) { throw "Run this from the unzipped folder: 'site\SchoolFlow.Web.exe' was not found next to the script." }
if (-not (Test-Path (Join-Path $SitePath 'SchoolFlow.Web.exe'))) { throw "$SitePath does not look like the SchoolFlow site folder (no SchoolFlow.Web.exe in it)." }
if (-not (Test-Path (Join-Path $SitePath 'appsettings.Production.json'))) { throw "$SitePath has no appsettings.Production.json. This script is for updating a site that is already set up." }

$offline = Join-Path $SitePath 'app_offline.htm'
$keepSettings = Test-Path (Join-Path $SitePath 'appsettings.json')
Set-Content -Path $offline -Value '<html><body style="font-family:sans-serif"><h1>Updating</h1><p>This site is being updated and will be back shortly.</p></body></html>'

try {
  Start-Sleep -Seconds 6   # IIS notices app_offline.htm, stops the site and releases the program files
  foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
    if ($keepSettings -and $item.Name -eq 'appsettings.json') {
      Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $SitePath 'appsettings.json.new') -Force
      continue
    }
    Copy-Item -LiteralPath $item.FullName -Destination $SitePath -Recurse -Force
  }
}
catch {
  Write-Host ''
  Write-Host "The update did not finish: $($_.Exception.Message)" -ForegroundColor Red
  Write-Host 'The site has been left OFFLINE (app_offline.htm is still in place) so it does not run on a mix of old and new files.' -ForegroundColor Red
  Write-Host 'Usually a file is still in use. Wait a minute (or recycle the SchoolFlow app pool in IIS Manager), then run this script again.' -ForegroundColor Yellow
  exit 1
}

Remove-Item -LiteralPath $offline -ErrorAction SilentlyContinue
Write-Host "Files updated in $SitePath. Open the site's /healthz address: it should say ok." -ForegroundColor Green
if ($keepSettings) { Write-Host 'Your appsettings.json was kept; the new one is saved beside it as appsettings.json.new.' }
