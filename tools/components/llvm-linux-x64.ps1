param(
	[Parameter(Mandatory)] [string] $Output,
	[Parameter(Mandatory)] [string] $Work
)

. "$PSScriptRoot/Common.ps1"
. "$PSScriptRoot/Upstream.ps1"

if (-not $IsLinux) {
	throw 'llvm-linux-x64 links libLLVM.so against the build machine''s C++ runtime, so it must be built on Linux'
}

$mingw = @(Get-ChildItem (Get-Extracted -Name LlvmMingw -Work $Work) -Directory)[0].FullName
Copy-Files -From "$mingw/bin" -Names 'lld', 'llvm-ar', 'llvm-lib' -To $Output
Copy-Files -From "$mingw/lib" -Names 'libLLVM.so.23.1' -To $Output
New-Item -ItemType Directory -Force "$Output/licenses/llvm" | Out-Null
Copy-Item "$mingw/LICENSE.TXT" "$Output/licenses/llvm"

$gnu = Join-Path $Work 'linux-x64-gnu'
if (-not (Test-Path "$gnu/sysroot")) {
	& "$PSScriptRoot/linux-x64-gnu.ps1" -Output $gnu -Work $Work
}

$llvm = @(Get-ChildItem (Get-Extracted -Name LlvmLinux -Work $Work -Members '--wildcards', '*/lib/libLLVM*.a') -Directory)[0].FullName
$excluded = '^libLLVM(BOLT.*|Exegesis.*|CFIVerify|Diff|OptDriver|TableGen.*|WindowsManifest|FuzzerCLI|FuzzMutate|LineEditor|Debuginfod|SupportLSP|Extensions|LTO|DTLTO)\.a$'
$archives = @(Get-ChildItem "$llvm/lib" -Filter 'libLLVM*.a' | Where-Object { $_.Name -notmatch $excluded } |
	ForEach-Object FullName)

$system = '/usr/lib/x86_64-linux-gnu'
$env:LD_LIBRARY_PATH = $Output
& "$Output/lld" -flavor gnu -shared -m elf_x86_64 -soname libLLVM.so.22.1 -o "$Output/libLLVM.so" `
	--hash-style=gnu --build-id -z relro -z now -z noexecstack --no-undefined "--sysroot=$gnu/sysroot" `
	"$gnu/sysroot/usr/lib64/crti.o" "$gnu/compiler-rt/clang_rt.crtbegin.o" `
	--whole-archive @archives --no-whole-archive `
	"$system/libstdc++.so.6" "$system/libgcc_s.so.1" "$system/libz.so.1" "$system/libzstd.so.1" `
	-L "$gnu/sysroot/usr/lib64" -L "$gnu/sysroot/lib64" -lc -lm `
	"$gnu/compiler-rt/libclang_rt.builtins.a" "$gnu/compiler-rt/clang_rt.crtend.o" "$gnu/sysroot/usr/lib64/crtn.o"

if ($LASTEXITCODE -ne 0) {
	throw 'Linking libLLVM.so failed'
}
