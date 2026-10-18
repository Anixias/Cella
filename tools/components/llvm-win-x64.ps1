param(
	[Parameter(Mandatory)] [string] $Output,
	[Parameter(Mandatory)] [string] $Work
)

. "$PSScriptRoot/Common.ps1"
. "$PSScriptRoot/Upstream.ps1"

$llvm = Get-Extracted -Name LlvmWindows -Work $Work
$bin = Split-Path -Parent (Find-File -Root $llvm -Pattern 'lld.exe')
Copy-Files -From $bin -Names 'lld.exe', 'llvm-ar.exe', 'llvm-lib.exe' -To $Output

$package = Get-Extracted -Name LibLlvm -Work $Work
Copy-Item (Join-Path $package 'runtimes/win-x64/native/LLVM-C.dll') $Output

New-Item -ItemType Directory -Force "$Output/licenses/llvm" | Out-Null
Copy-Item (Find-File -Root $llvm -Pattern 'LICENSE.TXT') "$Output/licenses/llvm"
