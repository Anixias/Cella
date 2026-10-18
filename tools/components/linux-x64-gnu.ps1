param(
	[Parameter(Mandatory)] [string] $Output,
	[Parameter(Mandatory)] [string] $Work
)

. "$PSScriptRoot/Common.ps1"
. "$PSScriptRoot/Upstream.ps1"

$glibc = Get-Extracted -Name Glibc -Work $Work
$devel = Get-Extracted -Name GlibcDevel -Work $Work
$runtime = Get-Extracted -Name CompilerRt -Work $Work

Copy-Files -From "$glibc/usr/lib64" -Names 'libc.so.6', 'libm.so.6', 'libmvec.so.1', 'ld-linux-x86-64.so.2' `
	-To "$Output/sysroot/lib64"

Copy-Files -From "$devel/usr/lib64" -Names 'crt1.o', 'Scrt1.o', 'crti.o', 'crtn.o', 'libc.so', 'libm.so',
	'libc_nonshared.a' -To "$Output/sysroot/usr/lib64"

$builtins = Split-Path -Parent (Find-File -Root "$runtime/usr/lib/clang" -Pattern 'libclang_rt.builtins.a')
Copy-Files -From $builtins -Names 'clang_rt.crtbegin.o', 'clang_rt.crtend.o', 'libclang_rt.builtins.a' `
	-To "$Output/compiler-rt"

New-Item -ItemType Directory -Force "$Output/licenses/glibc", "$Output/licenses/compiler-rt" | Out-Null
Copy-Item "$glibc/usr/share/licenses/glibc/*" "$Output/licenses/glibc"
Copy-Item (Find-File -Root "$runtime/usr/share/licenses" -Pattern 'LICENSE.TXT') "$Output/licenses/compiler-rt"
