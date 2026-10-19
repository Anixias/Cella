param(
	[Parameter(Mandatory)] [string] $Output,
	[Parameter(Mandatory)] [string] $Work
)

. "$PSScriptRoot/Common.ps1"
. "$PSScriptRoot/Upstream.ps1"

$mingw = @(Get-ChildItem (Get-Extracted -Name LlvmMingw -Work $Work) -Directory)[0].FullName
$libraries = Join-Path $mingw 'x86_64-w64-mingw32/lib'
New-Item -ItemType Directory -Force "$Output/mingw", "$Output/compiler-rt", "$Output/licenses/mingw-w64",
	"$Output/licenses/compiler-rt" | Out-Null

Get-ChildItem $libraries -File |
	Where-Object {
		$_.Name -notlike '*.dll.a' -and $_.Name -notmatch '^(libc\+\+|libunwind)' -and $_.Extension -notin '.la', '.json'
	} |
	Copy-Item -Destination "$Output/mingw"

Copy-Item (Find-File -Root (Join-Path $mingw 'lib/clang') -Pattern 'libclang_rt.builtins-x86_64.a') "$Output/compiler-rt"
Copy-Item "$mingw/x86_64-w64-mingw32/share/mingw32/*" "$Output/licenses/mingw-w64"
Copy-Item "$mingw/LICENSE.TXT" "$Output/licenses/compiler-rt"
