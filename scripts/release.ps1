# Builds the release zip: a self-contained Windows build that needs no .NET install.
# Usage: powershell -File scripts/release.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version
$out = Join-Path $root "release\BluelineStudio-$version-win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

dotnet publish "$root\src\Blueline.App" -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'app publish failed' }
dotnet publish "$root\src\Blueline.Cli" -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=none -o "$out\cli"
if ($LASTEXITCODE -ne 0) { throw 'cli publish failed' }

Copy-Item "$root\README.md", "$root\CHANGELOG.md", "$root\docs\USER_GUIDE.md" $out
if (Test-Path "$root\LICENSE") { Copy-Item "$root\LICENSE" $out }

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256"
"{0}  {1:N1} MB" -f $zip, ((Get-Item $zip).Length / 1MB)
"SHA256 $hash"
