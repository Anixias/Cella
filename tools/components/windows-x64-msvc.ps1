param(
	[Parameter(Mandatory)] [string] $Output,
	[Parameter(Mandatory)] [string] $Work
)

. "$PSScriptRoot/Common.ps1"
. "$PSScriptRoot/Upstream.ps1"

$llvm = Get-Extracted -Name LlvmWindows -Work $Work
New-Item -ItemType Directory -Force "$Output/compiler-rt", "$Output/licenses/compiler-rt" | Out-Null
Copy-Item (Find-File -Root $llvm -Pattern 'clang_rt.builtins-x86_64.lib') "$Output/compiler-rt"
Copy-Item (Find-File -Root $llvm -Pattern 'LICENSE.TXT') "$Output/licenses/compiler-rt"
