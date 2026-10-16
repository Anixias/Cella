# <img src="cella.png" alt="Cella icon" width="64px" style="vertical-align: bottom"/> Cella

Cella is a systems programming language. It works at the level of C: raw pointers, no garbage collector, no runtime. On top of that it adds ownership and destructors, borrowed parameters, enums with payloads, generics, traits and modules. The compiler is written in C# and generates code with LLVM.

The compiler builds executables and shared or static libraries for 64-bit Windows and Linux, from either system. There is no standard library yet, so programs call C functions through `ext` declarations. The language is still changing and has no release.

## Example

```cella
mod shapes

ext
{
	printf: fun(format: cstr, ...) -> i32
	malloc: fun(size: usize) -> ptr[f64]
	free: fun(memory: ptr[f64])
}

Shape: enum
{
	circle(radius: f64)
	rect(width: f64, height: f64)
}

area: fun(shape: Shape) -> f64 = match shape
{
	circle(val r) => 3.14159 * r * r
	rect(val w, val h) => w * h
}

Areas: rec
{
	data: ptr[f64]
	count: usize

	new: op(count: usize)
	{
		self.data = malloc(count * sizeOf(f64))
		self.count = count
	}

	drop: op
	{
		printf("freeing %zu areas\n", self.count)
		free(self.data)
	}
}

fill: fun(mut areas: Areas, shapes: array[Shape, 3])
{
	for i in 0..areas.count
	{
		areas.data[i] = area(shapes[i])
	}
}

total: fun(imm areas: Areas) -> f64
{
	var sum = 0.0
	for i in 0..areas.count
	{
		sum += areas.data[i]
	}

	ret sum
}

main: fun -> i32
{
	val shapes = [Shape.circle(1.0), Shape.rect(2.0, 3.0), Shape.rect(0.5, 4.0)]
	var areas = Areas(3)
	fill(mut areas, shapes)
	printf("total %.2f\n", total(imm areas))
	ret 0
}
```

```
total 11.14
freeing 3 areas
```

## Memory and checks

Every value has one owner. A type with a `drop` operator runs it when its owner's scope ends. A parameter's mode says how it takes its argument: no mode copies, `imm` and `mut` borrow, and `own` takes ownership. Calls repeat the mode, as in `fill(mut areas, shapes)` above, and moving a value out of a variable is written `own`.

The compiler reports these as errors:

- using a value after it was moved ("Cannot use moved values")
- moving a value without writing `own` ("Cannot move 'Buffer' values implicitly")
- borrowing an argument without writing `imm` or `mut` ("Cannot borrow arguments implicitly")
- returning a borrow of a local variable, or storing a borrow in a record that isn't declared `ref rec`

As in C, the compiler doesn't check aliasing between borrows (`both(mut n, mut n)` compiles), invalidation, data races, or anything done through `ptr[T]`. A variable declared without a value starts zeroed. Generated code never allocates; heap memory comes from whatever the program calls, like `malloc` above.

Array indexing is bounds checked in every build, and integer overflow is checked in debug builds. Both can be changed per project. A failed check calls the `panic` operator the file can see, or traps if there isn't one.

The language also has methods, operators and properties on records, generic constraints, traits with `impl` and `dyn[Trait]` values, lambdas (`fun(x) = x * base`), and `when` for conditional compilation.

## Building

You need the .NET 10 SDK and a toolchain folder, which isn't in this repository. It holds the linker for each host system and the libraries for each target:

```
hosts/win-x64/                lld.exe, llvm-ar.exe, llvm-lib.exe
hosts/linux-x64/              lld, llvm-ar, llvm-lib and their libLLVM.so.23.1; libLLVM.so (LLVM 22.1.8) for the compiler
toolchains/linux-x64-gnu/     glibc 2.34 sysroot, compiler-rt
toolchains/windows-x64-gnu/   MinGW-w64 UCRT libraries, compiler-rt
toolchains/windows-x64-msvc/  MSVC and Windows SDK libraries, compiler-rt
```

Set `CellaToolchainDirectory` to that folder and build. The build copies the current system's `hosts` tools next to `cella` and the `toolchains` folder beside them.

```
dotnet build src/csharp/Cella.sln
```

Windows targets link with the MSVC libraries when `toolchains/windows-x64-msvc` exists and with MinGW-w64 otherwise. The MSVC and Windows SDK libraries can't be redistributed, so you have to supply them yourself.

Running the compiler on Linux needs ICU (`libicu`) and the libstdc++ from GCC 12 or later. Linux programs it builds need glibc 2.34 or later.

## Usage

```
cella <project file or folder> [--release] [--target windows-x64|windows-x64-gnu|linux-x64]
```

The target defaults to the system you're on. Other options: `--verbose`, `--hints`, `--flag <name>[=true|false]`, `--emit-ir`, `--emit-asm`.

A project is a `.celp` file in TOML:

```toml
OutputType = "Executable"

[[ProjectReferences]]
Path = 'geometry/geometry.celp'

[When.release]
BoundsChecks = false
```

`OutputType` is `Executable`, `WindowedExecutable`, `SharedLibrary` or `StaticLibrary`. Other settings are `AssemblyName`, `SystemLinkPreference`, `BoundsChecks`, `OverflowChecks`, `DebugInfo`, `Referenceable`, `Flags` and `Imports`. Output goes to the project's `bin` folder.

## Tests

```
dotnet test --project src/csharp/Cella.Tests/Cella.Tests.csproj
```

The tests are golden files in `src/csharp/Cella.Tests/Cases`. Each case is compiled and run for every target it has expected output for. On Windows, Linux programs run through WSL and are skipped without it. Cases that include C files need clang, taken from `CELLA_TEST_CLANG`, `PATH`, or the default LLVM install on Windows, and are skipped without it.

## License

Cella is licensed under the [MIT License](LICENSE).
