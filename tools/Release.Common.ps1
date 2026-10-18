Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Repository = Split-Path -Parent $PSScriptRoot
$IndexUrl = 'https://anixias.github.io/Cella/index.json'
$ReleaseUrl = 'https://github.com/Anixias/Cella/releases/download'
$Hosts = @('win-x64', 'linux-x64')

function Get-CellaVersion {
	(Get-Content (Join-Path $Repository 'version.txt') -Raw).Trim()
}

function Get-Components {
	Get-Content (Join-Path $Repository 'components.json') -Raw | ConvertFrom-Json -AsHashtable
}

function Get-HostName {
	$os = if ($IsWindows) { 'win' } else { 'linux' }
	"$os-$([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())"
}

function Get-ArchiveExtension {
	param([Parameter(Mandatory)] [string] $Name)

	if ($Name -match '(^|-)linux-x64$' -and $Name -ne 'linux-x64-gnu') { '.tar.gz' } else { '.zip' }
}

function Get-VersionAsset {
	param([Parameter(Mandatory)] [string] $Version, [Parameter(Mandatory)] [string] $HostName)

	"cella-$Version-$HostName$(Get-ArchiveExtension $HostName)"
}

function Get-ComponentAsset {
	param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $Version)

	"$Name-$Version$(Get-ArchiveExtension $Name)"
}

function Get-LauncherAsset {
	param([Parameter(Mandatory)] [string] $HostName)

	if ($HostName -like 'win-*') { "cella-launcher-$HostName.exe" } else { "cella-launcher-$HostName" }
}

function Get-Tar {
	if ($IsWindows) { Join-Path $env:SystemRoot 'System32/tar.exe' } else { 'tar' }
}

function New-Archive {
	param([Parameter(Mandatory)] [string] $Directory, [Parameter(Mandatory)] [string] $Path)

	if (Test-Path $Path) {
		Remove-Item -Force $Path
	}

	New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
	if ($Path.EndsWith('.zip')) {
		Add-Type -AssemblyName System.IO.Compression.FileSystem
		[IO.Compression.ZipFile]::CreateFromDirectory($Directory, $Path, [IO.Compression.CompressionLevel]::Optimal,
			$false)
		return
	}

	& (Get-Tar) -czf $Path -C $Directory .
	if ($LASTEXITCODE -ne 0) {
		throw "Couldn't create $Path"
	}
}

function Expand-Asset {
	param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Destination)

	if (Test-Path $Destination) {
		Remove-Item -Recurse -Force $Destination
	}

	New-Item -ItemType Directory -Force $Destination | Out-Null
	if ($Path.EndsWith('.zip')) {
		Add-Type -AssemblyName System.IO.Compression.FileSystem
		[IO.Compression.ZipFile]::ExtractToDirectory($Path, $Destination)
		return
	}

	& (Get-Tar) -xzf $Path -C $Destination
	if ($LASTEXITCODE -ne 0) {
		throw "Couldn't extract $Path"
	}
}

function Get-FileEntry {
	param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Url)

	[ordered]@{
		url = $Url
		sha256 = (Get-FileHash -Algorithm SHA256 $Path).Hash.ToLowerInvariant()
		size = (Get-Item $Path).Length
	}
}
