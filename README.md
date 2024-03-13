# <img src="cella.png" alt="Cella icon" width="64px"/> Cella

[Cella](https://anixias.github.io/Cella-Site/index.html) is a minimalistic, systems programming language inspired by Rust, focusing on value semantics, effect systems, immutability by default, and purity by default. It aims to provide a clean, consistent syntax with a strong emphasis on quality of life and developer productivity.

## Examples

### Hello World:

```cella
mod helloWorld

use std.io.Console

main: entry(args: String[]): !{io} Int32
{
	Console.println("Hello, World!")
	ret 0
}
```

## Key Features

- **Value Semantics**: Cella embraces value semantics, ensuring that data is stored and copied in a predictable and efficient manner.
- **Effect Systems**: The language incorporates an effect system to ensure purity by default and provide fine-grained control over side effects.
- **Immutability by Default**: Variables are immutable by default, promoting a more functional programming style and reducing the risk of unintended mutations.
- **Purity by Default**: Thanks to the effect system, Cella code is pure by default, making it easier to reason about and parallelize.
- **Minimal Runtime Overhead**: Cella employs static memory analysis and a combination of compile-time and runtime garbage collection to minimize runtime overhead.
- **LLVM Backend**: The language targets the LLVM compiler infrastructure, enabling efficient code generation and cross-platform support.
- **Consistent Syntax**: Cella's syntax is designed to be clean, consistent, and easy to learn, reducing cognitive overhead for developers.
- **Quality of Life**: Cella features numerous syntax sugar constructs and quality-of-life improvements to enhance developer productivity.
- **Simple Build System**: Cella provides a simple and streamlined build system, making it easy to build and manage projects.
- **Simple Package Management**: Managing dependencies and packages in Cella is straightforward, thanks to its simple package management system.
- **Cross-Platform Support**: Cella is designed to run on a wide range of platforms, enabling developers to write code that can be easily ported and deployed.

## Getting Started

The Cella compiler is currently under active development, and a working implementation is not yet available. Stay tuned for updates as the project progresses.

## Contributing

Contributions to Cella are welcome! If you're interested in getting involved, please check out the [Contributing Guidelines](CONTRIBUTING.md) for more information.

## License

Cella is licensed under the [MIT License](LICENSE).
