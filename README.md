# <img src="cella.png" alt="Cella icon" width="64px" style="vertical-align: bottom"/> Cella

[Cella](https://anixias.github.io/Cella-Site/index.html) is a minimalistic, systems programming language inspired by Rust, focusing on value semantics, effect systems, immutability by default, and purity by default. It aims to provide a clean, consistent syntax with a strong emphasis on quality of life and developer productivity.

## Examples

### Hello World:

```cella
mod helloWorld

use std.io.Console

main: entry(args: String[]): !io Int32
{
	Console.println("Hello, World!")
	ret 0
}
```

## Key Features

- **Value Semantics**: Embraces value semantics, ensuring that data is stored and copied in a predictable and efficient manner.
- **Semantic Effects**: Ensures purity by default and provides compile-time safety over side effects.
- **Immutability by Default**: Reduces the risk of unintended mutations by offering default immutability.
- **Minimal Runtime Overhead**: Employs static memory analysis and a combination of compile-time and runtime garbage collection to minimize runtime overhead.
- **LLVM Backend**: Targets the LLVM compiler infrastructure, enabling efficient code generation and cross-platform support.
- **Quality of Life**: Features numerous syntax sugar constructs and quality-of-life improvements to enhance developer productivity.
- **Simple Build System**: Provides a simple and streamlined build system, making it easy to build and manage projects.

## Getting Started

The Cella compiler is currently under active development, and a working implementation is not yet available. Stay tuned for updates as the project progresses.

## Contributing

Contributions to Cella are welcome! If you're interested in getting involved, please check out the [Contributing Guidelines](CONTRIBUTING.md) (coming soon) for more information.

## License

Cella is licensed under the [MIT License](LICENSE).
