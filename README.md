# <img src="cella.png" alt="Cella icon" width="64px" style="vertical-align: bottom"/> Cella

[Cella](https://anixias.github.io/Cella-Site/index.html) is a systems programming language. It works at the level of C: raw pointers, no garbage collector, no runtime. On top of that it adds ownership and destructors, borrowed parameters, enums with payloads, generics, traits and modules. The compiler is written in C# and generates code with LLVM.

The compiler builds executables and shared or static libraries for 64-bit Windows and Linux, from either system. The standard library has only just started, so programs call C functions through `ext` declarations. The language is still changing and hasn't had a release yet.

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

area: fun(shape: Shape) -> f64 => match shape
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

Array indexing is bounds checked in every build, and integer overflow is checked in debug builds. Both can be changed per project. A failed check calls the `panic` operator the file can see, or traps if there isn't one. The standard library's `panic` is visible everywhere: it prints the message and location and ends the program at once, as a crash, without running destructors.

The language also has methods, operators and properties on records, generic constraints, traits with `impl` and `dyn[Trait]` values, lambdas (`fun(x) => x * base`), and `when` for conditional compilation.

## Installing

Cella installs a small launcher named `cella`, which manages compiler versions and runs the right one for each project. On Windows, run this in PowerShell:

```
irm https://anixias.github.io/Cella/install.ps1 | iex
```

On Linux:

```
curl -fsSL https://anixias.github.io/Cella/install.sh | sh
```

The installer puts everything in `C:\cella` or `~/.cella`, or in `CELLA_HOME` if it's set. It adds the folder to your PATH on Windows and links `~/.local/bin/cella` on Linux, then installs the latest version.

Windows programs link with Visual Studio's C++ build tools and the Windows SDK when they're installed, and with MinGW-w64 otherwise. On Linux, the compiler needs ICU (`libicu`) and the libstdc++ from GCC 12 or later. Linux programs it builds need glibc 2.34 or later.

## Usage

```
cella build [project file or folder] [--release] [--target windows-x64|windows-x64-gnu|linux-x64]
```

The target defaults to the system you're on. Other options: `--verbose`, `--hints`, `--flag <name>[=true|false]`, `--emit-ir`, `--emit-asm`.

The launcher's own commands:

```
cella install [version]     Install a version, the latest when none is given
cella uninstall <version>   Remove a version
cella list                  List installed and available versions
cella self update           Update the launcher
cella self uninstall        Remove Cella completely
```

A project is a `.celp` file in TOML:

```toml
OutputType = "Executable"
CellaVersion = "0.1.0"

[[ProjectReferences]]
Path = 'geometry/geometry.celp'

[When.release]
BoundsChecks = false
```

`CellaVersion` pins the version that builds the project. Without a pin, the newest installed version builds it. `cella +0.1.0 build` uses another installed version for one command.

`OutputType` is `Executable`, `WindowedExecutable`, `SharedLibrary` or `StaticLibrary`. Other settings are `AssemblyName`, `SystemLinkPreference`, `BoundsChecks`, `OverflowChecks`, `DebugInfo`, `Referenceable`, `Flags`, `Imports`, `Prelude` and `StandardLibrary`. Output goes to the project's `bin` folder.

Every project references the standard library. Set `StandardLibrary = "Core"` to use only the part that doesn't need an operating system, or `"None"` to leave it out.

## Editors

Each release includes a VS Code extension and a Rider plugin. They highlight Cella code and run the language server (`cella lsp`) for diagnostics, hover, go-to-definition, find usages and completion.

## Building

You need the .NET 10 SDK, PowerShell 7, and a toolchain folder holding the linker and the libraries for each target. To fetch one, run:

```
pwsh tools/fetch-components.ps1 -Output <folder>
```

Set `CellaToolchainDirectory` to that folder and build. The build puts the linker next to the compiler, in `src/csharp/Cella.Compiler/bin/<configuration>/net10.0/<runtime>`, with the `toolchains` folder beside it.

```
dotnet build src/csharp/Cella.sln
```

[docs/maintaining.md](docs/maintaining.md) covers releases, components, and adding targets and hosts.

## Tests

```
dotnet test --project src/csharp/Cella.Tests/Cella.Tests.csproj
```

The tests are golden files in `src/csharp/Cella.Tests/Cases`, plus the standard library's cases in `src/std/tests`. Each case is compiled and run for every target it has expected output for. On Windows, Linux programs run through WSL and are skipped without it. Cases that include C files need clang, taken from `CELLA_TEST_CLANG`, `PATH`, or the default LLVM install on Windows, and are skipped without it.

## License

Cella is licensed under the [MIT License](LICENSE).
