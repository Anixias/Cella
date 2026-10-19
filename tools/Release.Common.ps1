Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Repository = Split-Path -Parent $PSScriptRoot
$IndexUrl = 'https://anixias.github.io/Cella/index.json'
$ReleaseUrl = 'https://github.com/Anixias/Cella/releases/download'
$Hosts = [ordered]@{
	'win-x64' = @{ Archive = '.zip'; Executable = '.exe' }
	'linux-x64' = @{ Archive = '.tar.gz'; Executable = '' }
}

function Get-CellaVersion {
	(Get-Content (Join-Path $Repository 'version.txt') -Raw).Trim()
}

function Get-ComponentVersions {
	Get-Content (Join-Path $Repository 'components.json') -Raw | ConvertFrom-Json -AsHashtable
}

function Get-Components {
	$versions = Get-ComponentVersions
	foreach ($kind in 'llvm', 'toolchains') {
		foreach ($entry in $versions[$kind].GetEnumerator()) {
			[pscustomobject]@{ Kind = $kind; Name = $entry.Key; Version = $entry.Value }
		}
	}
}

function Get-Component {
	param([Parameter(Mandatory)] [string] $Kind, [Parameter(Mandatory)] [string] $Name)

	$component = Get-Components | Where-Object { $_.Kind -eq $Kind -and $_.Name -eq $Name }
	if (-not $component) {
		throw "components.json lists no $Kind component named '$Name'"
	}

	$component
}

function Get-HostName {
	$os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } else { throw "Cella doesn't build on this operating system" }
	"$os-$([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())"
}

function Test-NotFound {
	param([Parameter(Mandatory)] [Management.Automation.ErrorRecord] $ErrorRecord)

	$ErrorRecord.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException] -and
		$ErrorRecord.Exception.Response.StatusCode -eq [Net.HttpStatusCode]::NotFound
}

function Get-VersionAsset {
	param([Parameter(Mandatory)] [string] $Version, [Parameter(Mandatory)] [string] $HostName)

	"cella-$Version-$HostName$($Hosts[$HostName].Archive)"
}

function Get-ComponentRecipe {
	param([Parameter(Mandatory)] $Component)

	if ($Component.Kind -eq 'llvm') {
		"tools/components/llvm-$($Component.Name).ps1"
	}
	else {
		"tools/components/$($Component.Name).ps1"
	}
}

function Get-ComponentAsset {
	param([Parameter(Mandatory)] $Component)

	if ($Component.Kind -eq 'llvm') {
		"llvm-$($Component.Name)-$($Component.Version)$($Hosts[$Component.Name].Archive)"
	}
	else {
		"$($Component.Name)-$($Component.Version).zip"
	}
}

function Get-LauncherAsset {
	param([Parameter(Mandatory)] [string] $HostName)

	"cella-launcher-$HostName$($Hosts[$HostName].Executable)"
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
