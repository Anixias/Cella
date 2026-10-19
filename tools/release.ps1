#Requires -Version 7
param(
	[Parameter(Mandatory, Position = 0)] [ValidateSet('prepare', 'publish', 'verify')] [string] $Command,
	[string] $Version,
	[string] $Next,
	[string] $Tag,
	[string] $Notes,
	[string] $Remote = 'origin'
)

. "$PSScriptRoot/Release.Common.ps1"

$Branch = 'main'
$ChangelogPath = Join-Path $Repository 'CHANGELOG.md'
$VersionPath = Join-Path $Repository 'version.txt'
$VersionPattern = '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$'

function Invoke-Git {
	$output = & git -C $Repository @args 2>&1
	if ($LASTEXITCODE -ne 0) {
		throw "git $($args -join ' ') failed: $output"
	}

	$output
}

function Assert-ReadyToRelease {
	if (Invoke-Git status --porcelain) {
		throw 'The working tree has uncommitted changes'
	}

	$current = Invoke-Git rev-parse --abbrev-ref HEAD
	if ($current -ne $Branch) {
		throw "Releases are made from '$Branch', not '$current'"
	}
}

function Get-LastTag {
	$tag = & git -C $Repository describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null
	if ($LASTEXITCODE -eq 0) { $tag } else { $null }
}

function Get-ChangelogEntries {
	param([string] $Since)

	$range = if ($Since) { "$Since..HEAD" } else { 'HEAD' }
	$log = (& git -C $Repository log --reverse "--format=%b%x1e" $range) -join "`n"
	if ($LASTEXITCODE -ne 0) {
		throw "git log $range failed"
	}

	foreach ($description in $log -split [char]0x1e) {
		$marker = $description.IndexOf('[~]')
		if ($marker -lt 0) {
			continue
		}

		$entry = $description.Substring($marker + 3).Trim()
		if ($entry) {
			$entry
		}
	}
}

function Get-ChangelogSection {
	param([Parameter(Mandatory)] [string] $ForVersion)

	if (-not (Test-Path $ChangelogPath)) {
		return $null
	}

	$lines = Get-Content $ChangelogPath
	$start = -1
	for ($i = 0; $i -lt $lines.Count; $i++) {
		if ($lines[$i] -match "^## $([regex]::Escape($ForVersion))(\s|$)") {
			$start = $i
			break
		}
	}

	if ($start -lt 0) {
		return $null
	}

	$end = $start + 1
	while ($end -lt $lines.Count -and $lines[$end] -notmatch '^## ') {
		$end++
	}

	($lines[($start + 1)..($end - 1)] -join "`n").Trim()
}

function Get-NextVersion {
	param([Parameter(Mandatory)] [string] $Released)

	if ($Released -match '-') {
		return ($Released -replace '-.*$', '') + '-dev'
	}

	$parts = $Released.Split('.')
	"$($parts[0]).$([int]$parts[1] + 1).0-dev"
}

function Invoke-Prepare {
	Assert-ReadyToRelease
	$current = Get-CellaVersion
	if ($current -notmatch '-dev$') {
		throw "version.txt already holds the release version $current; finish it with 'release.ps1 publish'"
	}

	$release = if ($Version) { $Version } else { $current -replace '-dev$', '' }
	if ($release -notmatch $VersionPattern -or $release -match '-dev$') {
		throw "'$release' is not a valid release version"
	}

	if (Get-ChangelogSection -ForVersion $release) {
		throw "CHANGELOG.md already has a section for $release"
	}

	$lastTag = Get-LastTag
	$entries = @(Get-ChangelogEntries -Since $lastTag)
	$section = "## $release ($((Get-Date).ToString('yyyy-MM-dd')))`n`n" + ($entries -join "`n`n")
	$existing = if (Test-Path $ChangelogPath) { (Get-Content $ChangelogPath -Raw).TrimEnd() } else { '# Changelog' }
	$changelog = if ($existing -match '^(# [^\n]*\n)') {
		$Matches[1] + "`n" + $section.TrimEnd() + "`n`n" + $existing.Substring($Matches[1].Length).TrimStart()
	}
	elseif ($existing -match '^# ') {
		$existing + "`n`n" + $section.TrimEnd()
	}
	else {
		"# Changelog`n`n" + $section.TrimEnd() + "`n`n" + $existing
	}

	Set-Content -Path $ChangelogPath -Value ($changelog.TrimEnd() + "`n") -NoNewline
	Set-Content -Path $VersionPath -Value "$release`n" -NoNewline

	if ($lastTag) {
		$changed = @(Invoke-Git diff --name-only $lastTag HEAD -- tools/components)
		$components = @(Get-Components)
		foreach ($file in $changed) {
			$component = $components | Where-Object { (Get-ComponentRecipe $_) -eq $file }
			if ($component -and $component.Version -ne $release) {
				Write-Warning "$file changed since $lastTag, but components.json still lists $($component.Version) for $($component.Kind) $($component.Name)"
			}
			elseif (-not $component) {
				Write-Warning "$file changed since $lastTag; check whether components that use it need a new version"
			}
		}
	}

	$counted = if ($entries.Count -eq 1) { '1 changelog entry' } else { "$($entries.Count) changelog entries" }
	Write-Host "Prepared $release with $counted since $(if ($lastTag) { $lastTag } else { 'the first commit' })"
	Write-Host "Edit CHANGELOG.md, commit, then run 'release.ps1 publish'"
}

function Invoke-Publish {
	Assert-ReadyToRelease
	$release = Get-CellaVersion
	if ($release -match '-dev$' -or $release -notmatch $VersionPattern) {
		throw "version.txt holds $release; run 'release.ps1 prepare' first"
	}

	if ($null -eq (Get-ChangelogSection -ForVersion $release)) {
		throw "CHANGELOG.md has no section for $release"
	}

	$tagName = "v$release"
	if (& git -C $Repository tag --list $tagName) {
		throw "The tag $tagName already exists"
	}

	if (Invoke-Git ls-remote --tags $Remote "refs/tags/$tagName") {
		throw "The tag $tagName already exists on $Remote"
	}

	foreach ($component in Get-Components) {
		if ($component.Version -eq $release -and -not (Test-Path (Join-Path $Repository (Get-ComponentRecipe $component)))) {
			throw "components.json builds $($component.Kind) $($component.Name) in $release, but it has no recipe"
		}
	}

	Invoke-Git tag -a $tagName -m "Cella $release" | Out-Null
	Invoke-Git push --atomic $Remote $Branch $tagName | Out-Null
	Write-Host "Tagged and pushed $tagName; GitHub is building the release"

	$following = if ($Next) { $Next } else { Get-NextVersion -Released $release }
	if ($following -notmatch '-dev$' -or $following -notmatch $VersionPattern) {
		throw "'$following' is not a valid development version"
	}

	Set-Content -Path $VersionPath -Value "$following`n" -NoNewline
	Invoke-Git commit -m "Begin $following" -- version.txt | Out-Null
	Write-Host "Committed $following locally; push it when ready"
}

function Invoke-Verify {
	if (-not $Tag) {
		throw 'verify needs -Tag'
	}

	$release = Get-CellaVersion
	if ($Tag -ne "v$release") {
		throw "The tag $Tag doesn't match version.txt ($release)"
	}

	$section = Get-ChangelogSection -ForVersion $release
	if ($null -eq $section) {
		throw "CHANGELOG.md has no section for $release"
	}

	if ($Notes) {
		Set-Content -Path $Notes -Value $section
	}

	$built = @(Get-Components | Where-Object Version -eq $release | Sort-Object Kind, Name |
		ForEach-Object { [ordered]@{ kind = $_.Kind; name = $_.Name } })
	ConvertTo-Json -InputObject $built -Compress
}

try {
	switch ($Command) {
		'prepare' { Invoke-Prepare }
		'publish' { Invoke-Publish }
		'verify' { Invoke-Verify }
	}
}
catch {
	[Console]::Error.WriteLine($_.Exception.Message)
	exit 1
}
