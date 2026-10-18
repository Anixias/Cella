$AlmaLinux = @(
	'https://repo.almalinux.org/almalinux/9'
	'https://repo.almalinux.org/almalinux/9.8'
	'https://vault.almalinux.org/9.8'
)

$Upstream = @{
	LlvmWindows = @{
		Urls = @('https://github.com/llvm/llvm-project/releases/download/llvmorg-22.1.8/clang+llvm-22.1.8-x86_64-pc-windows-msvc.tar.xz')
		Sha256 = 'd96c2cc1736f4eb7fa43cb9bbdf56d93551a9ae0a9aadb9c99c3c3b2b712a234'
	}
	LlvmLinux = @{
		Urls = @('https://github.com/llvm/llvm-project/releases/download/llvmorg-22.1.8/LLVM-22.1.8-Linux-X64.tar.xz')
		Sha256 = 'df0e1ecf16caf3489a272a5eea4eec9b0d82878f6477fa309504f918a0006384'
	}
	LlvmMingw = @{
		Urls = @('https://github.com/mstorsjo/llvm-mingw/releases/download/20261006/llvm-mingw-20261006-ucrt-ubuntu-22.04-x86_64.tar.xz')
		Sha256 = '5f9c6ed95b2d4bdb2869a488c5fd5857fbdabcf288a0aa3eb1da43f6a08d8ab4'
	}
	LibLlvm = @{
		Urls = @('https://api.nuget.org/v3-flatcontainer/libllvm.runtime.win-x64/22.1.8/libllvm.runtime.win-x64.22.1.8.nupkg')
		Sha256 = 'f20e6e2650fae840e2fee44ed234513de4a1c424c57929fb262a286567982864'
	}
	Glibc = @{
		Urls = @($AlmaLinux | ForEach-Object { "$_/BaseOS/x86_64/os/Packages/glibc-2.34-283.el9_8.x86_64.rpm" })
		Sha256 = 'ccaa3e5bae82c0ef04cdb9a6ec60bebe385bed77a29c1a10a7d32a3a0abff244'
	}
	GlibcDevel = @{
		Urls = @($AlmaLinux | ForEach-Object { "$_/AppStream/x86_64/os/Packages/glibc-devel-2.34-283.el9_8.x86_64.rpm" })
		Sha256 = '2859ecfe172fb8db2a8d71e8f50c4530ab39afc327e71ab4b6efc9e5278ca476'
	}
	CompilerRt = @{
		Urls = @($AlmaLinux | ForEach-Object { "$_/AppStream/x86_64/os/Packages/compiler-rt-21.1.8-2.el9.x86_64.rpm" })
		Sha256 = 'd1001b879ef0420e6d820fd9df2f5e257bdaa3c5e2cd07a3de7c4b80e908d463'
	}
}

function Get-Extracted {
	param(
		[Parameter(Mandatory)] [string] $Name,
		[Parameter(Mandatory)] [string] $Work,
		[string[]] $Members = @()
	)

	$source = $Upstream[$Name]
	$destination = Join-Path $Work "extracted/$Name"
	$marker = Join-Path $destination '.complete'
	if (Test-Path $marker) {
		return $destination
	}

	if (Test-Path $destination) {
		Remove-Item -Recurse -Force $destination
	}

	$archive = Get-Upstream -Urls $source.Urls -Sha256 $source.Sha256 -Work $Work
	Expand-Upstream -Archive $archive -Destination $destination -Members $Members
	New-Item -ItemType File $marker | Out-Null
	return $destination
}

function Find-File {
	param(
		[Parameter(Mandatory)] [string] $Root,
		[Parameter(Mandatory)] [string] $Pattern
	)

	$found = @(Get-ChildItem -Path $Root -Recurse -File -Filter $Pattern)
	if ($found.Count -eq 0) {
		throw "No '$Pattern' under $Root"
	}

	return $found[0].FullName
}
