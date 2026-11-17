[CmdletBinding()]
param(
    [string]$GamePath,
    [string]$UploaderPath,
    [string]$Remote = 'origin',
    [switch]$PublishGitHub,
    [switch]$PublishWorkshop
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$gamePathDefault = Join-Path $repo '..\..\..\..\common\Slay the Spire 2'
$workshopId = '3840913980'
$modName = 'MonsterIntentRandomizer'
$project = Join-Path $repo 'mod\MonsterIntentRandomizer.Mod.csproj'
$manifestPath = Join-Path $repo 'mod\MonsterIntentRandomizer.json'
$workshopSource = Join-Path $repo 'workshop'

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Program exited with code $LASTEXITCODE."
    }
}

if (-not $GamePath) { $GamePath = $gamePathDefault }
if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'data_sts2_windows_x86_64\sts2.dll') -PathType Leaf)) {
    throw 'Pass the Slay the Spire 2 installation directory with -GamePath.'
}
$GamePath = (Resolve-Path -LiteralPath $GamePath).Path

$manifest = Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
$version = $manifest.version
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -ne $projectXml.Project.PropertyGroup.Version) {
    throw 'Set the same numeric version in the mod manifest and project before building.'
}
$metadata = Get-Content -LiteralPath (Join-Path $workshopSource 'workshop.json') -Encoding UTF8 -Raw | ConvertFrom-Json

if ($PublishGitHub) {
    $gitArgs = @('-c', ('safe.directory=' + $repo.Replace('\', '/')), '-C', $repo)
    if (Invoke-Checked git ($gitArgs + @('status', '--porcelain'))) {
        throw 'Commit the intended release changes before pushing to GitHub.'
    }
    $branch = Invoke-Checked git ($gitArgs + @('branch', '--show-current'))
    if (-not $branch) {
        throw 'Check out the branch you want to push to GitHub.'
    }
}

if ($PublishWorkshop) {
    if (-not $UploaderPath) {
        $uploader = Get-Command ModUploader.exe -CommandType Application -ErrorAction SilentlyContinue
        if ($uploader) { $UploaderPath = $uploader.Source }
    }
    if (-not $UploaderPath) {
        throw 'Pass the official ModUploader.exe with -UploaderPath.'
    }
    $UploaderPath = (Resolve-Path -LiteralPath $UploaderPath).Path
}

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff')
$release = Join-Path $repo "dist\release\v$version-$stamp"
$package = Join-Path $release $modName
$workshop = Join-Path $release 'workshop'
$content = Join-Path $workshop 'content'
$zip = Join-Path $release "$modName-v$version.zip"

Invoke-Checked dotnet @('run', '--project', (Join-Path $repo 'MonsterIntentRandomizer.csproj'), '-c', 'Release', '--', '--check')
Invoke-Checked dotnet @('build', $project, '-c', 'Release', '-p:InstallMod=false', "-p:GamePath=$GamePath", "-p:PackageDir=$package")

New-Item -Path $content -ItemType Directory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $package "$modName.dll"), (Join-Path $package "$modName.json") -Destination $content
Copy-Item -LiteralPath (Join-Path $workshopSource 'image.png') -Destination $workshop
[IO.File]::WriteAllText((Join-Path $workshop 'mod_id.txt'), $workshopId)
$workshopUpdate = @{ changeNote = $metadata.changeNote } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $workshop 'workshop.json'), $workshopUpdate)
Compress-Archive -LiteralPath $package -DestinationPath $zip
Write-Host "ZIP $zip"
Write-Host "Workshop workspace $workshop"

if ($PublishGitHub) {
    Invoke-Checked git ($gitArgs + @('push', '--', $Remote, "HEAD:refs/heads/$branch"))
    Write-Host "GitHub branch pushed to $Remote/$branch."
}
if ($PublishWorkshop) {
    Push-Location -LiteralPath (Split-Path -Parent $UploaderPath)
    try {
        Invoke-Checked $UploaderPath @('upload', '-w', $workshop, '-i', $workshopId)
    }
    finally {
        Pop-Location
    }
}
