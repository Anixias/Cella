Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Upstream {
	param(
		[Parameter(Mandatory)] [string[]] $Urls,
		[Parameter(Mandatory)] [string] $Sha256,
		[Parameter(Mandatory)] [string] $Work
	)

	$downloads = Join-Path $Work 'downloads'
	New-Item -ItemType Directory -Force $downloads | Out-Null
	$name = [IO.Path]::GetFileName(([Uri]$Urls[0]).AbsolutePath)
	$path = Join-Path $downloads $name
	if ((Test-Path $path) -and (Get-FileHash -Algorithm SHA256 $path).Hash -eq $Sha256) {
		return $path
	}

	foreach ($url in $Urls) {
		Write-Host "Downloading $url"
		try {
			Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing
		}
		catch {
			Write-Host "  failed: $($_.Exception.Message)"
			continue
		}

		$actual = (Get-FileHash -Algorithm SHA256 $path).Hash
		if ($actual -eq $Sha256) {
			return $path
		}

		throw "$name has SHA-256 $actual, expected $Sha256"
	}

	throw "Couldn't download $name"
}

function Expand-Upstream {
	param(
		[Parameter(Mandatory)] [string] $Archive,
		[Parameter(Mandatory)] [string] $Destination,
		[string[]] $Members = @()
	)

	New-Item -ItemType Directory -Force $Destination | Out-Null
	if ($Archive -match '\.(zip|nupkg)$') {
		Add-Type -AssemblyName System.IO.Compression.FileSystem
		[IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Destination, $true)
		return
	}

	$tar = if ($IsWindows) { Join-Path $env:SystemRoot 'System32/tar.exe' } elseif ($Archive -match '\.rpm$') { 'bsdtar' } else { 'tar' }
	$arguments = @('-xf', $Archive, '-C', $Destination)
	$exclude = $null
	if ($IsWindows) {
		$links = @(Get-LinkEntries -Tar $tar -Archive $Archive)
		if ($links.Count -gt 0) {
			$exclude = New-TemporaryFile
			Set-Content -Path $exclude -Value $links
			$arguments += @('-X', $exclude.FullName)
		}
	}

	try {
		& $tar @arguments @Members
		if ($LASTEXITCODE -ne 0) {
			throw "Couldn't extract $Archive"
		}
	}
	finally {
		if ($exclude) {
			Remove-Item $exclude
		}
	}
}

function Get-LinkEntries {
	param(
		[Parameter(Mandatory)] [string] $Tar,
		[Parameter(Mandatory)] [string] $Archive
	)

	$names = @(& $Tar -tf $Archive)
	if ($LASTEXITCODE -ne 0) {
		throw "Couldn't list $Archive"
	}

	$details = @(& $Tar -tvf $Archive)
	if ($LASTEXITCODE -ne 0 -or $details.Count -ne $names.Count) {
		throw "Couldn't list $Archive"
	}

	for ($i = 0; $i -lt $names.Count; $i++) {
		if ($details[$i] -match '^[lh]') {
			$names[$i]
		}
	}
}

function Copy-Files {
	param(
		[Parameter(Mandatory)] [string] $From,
		[Parameter(Mandatory)] [string[]] $Names,
		[Parameter(Mandatory)] [string] $To
	)

	New-Item -ItemType Directory -Force $To | Out-Null
	foreach ($name in $Names) {
		$source = Join-Path $From $name
		if (-not (Test-Path $source)) {
			throw "Missing $source"
		}

		[IO.File]::Copy((Resolve-Path $source).Path, (Join-Path $To (Split-Path -Leaf $name)), $true)
	}
}
