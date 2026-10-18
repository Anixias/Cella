$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = if ($env:CELLA_HOME) { $env:CELLA_HOME } else { 'C:\cella' }
$base = 'https://github.com/Anixias/Cella/releases/latest/download'
$asset = 'cella-launcher-win-x64.exe'

if (-not [Environment]::Is64BitOperatingSystem) {
	throw 'Cella needs 64-bit Windows'
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force $temporary | Out-Null
try {
	Write-Host 'Downloading the Cella launcher'
	Invoke-WebRequest -Uri "$base/$asset" -OutFile (Join-Path $temporary $asset) -UseBasicParsing
	Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $temporary 'SHA256SUMS') -UseBasicParsing

	$expected = Get-Content (Join-Path $temporary 'SHA256SUMS') |
		Where-Object { $_ -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($asset))$" } |
		ForEach-Object { $Matches[1] }

	$actual = (Get-FileHash -Algorithm SHA256 (Join-Path $temporary $asset)).Hash
	if (-not $expected -or $actual -ne $expected) {
		throw "The launcher doesn't match its published checksum"
	}

	New-Item -ItemType Directory -Force $root | Out-Null
	$launcher = Join-Path $root 'cella.exe'
	Copy-Item (Join-Path $temporary $asset) $launcher -Force
}
finally {
	Remove-Item -Recurse -Force $temporary
}

& $launcher self install
if ($LASTEXITCODE -ne 0) {
	exit $LASTEXITCODE
}

& $launcher install
exit $LASTEXITCODE
