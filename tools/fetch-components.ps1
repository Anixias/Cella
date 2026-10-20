#Requires -Version 7
param(
	[Parameter(Mandatory)] [string] $Output,
	[string] $Built,
	[string] $Work = (Join-Path ([IO.Path]::GetTempPath()) 'cella-components')
)

. "$PSScriptRoot/Release.Common.ps1"

$hostName = Get-HostName
$index = try {
	Invoke-RestMethod -Uri $IndexUrl
}
catch {
	if (-not (Test-NotFound $_)) {
		throw
	}

	$null
}
$downloads = Join-Path $Work 'assets'
New-Item -ItemType Directory -Force $downloads | Out-Null

foreach ($component in Get-Components) {
	if ($component.Kind -eq 'llvm' -and $component.Name -ne $hostName) {
		continue
	}

	$asset = Get-ComponentAsset $component
	$archive = if ($Built) { Join-Path $Built $asset } else { '' }
	if (-not $archive -or -not (Test-Path $archive)) {
		$release = if ($index) { $index.versions.($component.Version) } else { $null }
		$published = if ($release) { $release.($component.Kind).($component.Name) } else { $null }
		if ($published) {
			$archive = Join-Path $downloads $asset
			Write-Host "Downloading $($published.url)"
			Invoke-WebRequest -Uri $published.url -OutFile $archive -UseBasicParsing
			if ((Get-FileHash -Algorithm SHA256 $archive).Hash -ne $published.sha256) {
				throw "$asset doesn't match its published checksum"
			}
		}
		else {
			& "$PSScriptRoot/build-component.ps1" -Kind $component.Kind -Name $component.Name -Output $downloads `
				-Work $Work
			$archive = Join-Path $downloads $asset
		}
	}

	$destination = if ($component.Kind -eq 'llvm') {
		Join-Path $Output "llvm/$($component.Version)"
	}
	else {
		Join-Path $Output "toolchains/$($component.Name)/$($component.Version)"
	}

	Expand-Asset -Path $archive -Destination $destination
	Write-Host "Installed $($component.Kind) $($component.Name) $($component.Version) into $destination"
}
