# Cella Style Standard

The standard library follows these rules, and they are the expected default for user code. Types are PascalCase and values are camelCase. The compiler checks none of these rules; the one exception is a hint on a `var` that never changes.

## Names at a glance

| Kind | Casing | Examples |
| --- | --- | --- |
| Types (records, enums, unions) | PascalCase | `Enemy`, `List[T]`, `Option[T]` |
| Built-in types | lowercase | `i32`, `f64`, `bool`, `str`, `ptr[T]`, `array[T, length]` |
| Traits | PascalCase | `Hash`, `Eq`, `Iterator`, `Display` |
| Built-in constraints | lowercase keywords | `copy`, `noref`, `default`, `ext` |
| Functions, methods, properties | camelCase | `addItem`, `isEmpty`, `withCapacity` |
| Variables, parameters, fields | camelCase | `playerCount`, `isVisible` |
| Constants | camelCase | `maxPlayers`, `gravity` |
| Enum cases | camelCase | `north`, `moveTo(x, y)` |
| Type parameters | single uppercase letters | `T`, `K`, `V`, `E` |
| Value parameters | camelCase | `capacity`, `length` |
| Module path segments | lowercase, camelCase when multi-word | `game.worldGen`, `std.collections` |
| Source files | camelCase | `worldGen.ce`, `main.ce` |
| Projects | camelCase | `gameServer.celp` |

## Types and traits

- **Built-in types keep their lowercase names:** `i32`, `f64`, `bool`, `str`, `cstr`, `ptr[T]`, `array[T, length]`, `view[T]`.
- **Every other type is PascalCase,** standard library included: `List`, `Option`, `String`, `Enemy`.
- **Traits are bare capability or role words in PascalCase:** `Hash`, `Eq`, `Ord`, `Display`, `Iterator`, `Shape`. Abbreviations are fine, as the keywords are abbreviated too. No `-able` adjectives (`Hashable`) and no `I` prefix (`IShape`).
- **Built-in constraints stay lowercase keywords:** `copy`, `noref`, `default`, `ext`, `atomic`.

```cella
Shape: trait
{
	area: fun(imm self) -> f64
}

Circle: rec impl Shape
{
	radius: f64

	area: fun(imm self) -> f64 => 3.14159 * self.radius * self.radius
}
```

## Functions, methods and properties

1. **camelCase** for functions, methods and properties.
2. **Booleans ask a question:** `isEmpty`, `hasValue`, `canRead`, `contains`, never a bare adjective like `empty`.
3. **Actions are verbs, values are nouns.** Something with side effects is named for what it does (`add`, `reserve`, `clear`, `remove`). Something without side effects is named for what it gives (`count`, `capacity`, `first`).
4. **Conversions use the conversion operators,** `as` for implicit and `new: op(self) -> T` for explicit, never `toX` methods.
5. **Named constructors are static functions named for how they build** (`empty`, `withCapacity`, `from`, `parse`). The plain constructor stays `new`, called as `List[T]()`.

```cella
Buffer: rec
{
	data: pvt ptr[i32]
	length: pvt set i32

	isEmpty: get(imm self) -> bool => self.length == 0

	new: op(length: i32)
	{
		self.data = malloc(usize(length) * sizeOf(i32))
		self.length = length
	}

	empty: fun -> Buffer => Buffer(0)

	clear: fun(mut self) => self.length = 0
}
```

## Values

Every value is camelCase: variables, parameters, fields, constants, trait constants and enum cases.

- **Boolean variables and fields take the same prefixes as boolean methods:** `isVisible`, `hasFocus`.
- **No prefixes or suffixes for members or privacy** (`_count`, `mCount`, `count_`). Fields are always reached through `self.`, and visibility comes from `pub`, `mod` and `pvt`.
- **Single letters only for loop indices and tiny lambdas:** `for i in 0..count`, `fun(x) => x * 2`.
- **Constants are camelCase too,** not SCREAMING\_CASE or PascalCase: `maxPlayers: const i32 = 4`, and in a type `array[Item, slotCount]`.
- **Enum cases are camelCase,** with or without a payload: `north`, `holdPosition`, `moveTo(x: i32, y: i32)`, used as `Command.moveTo(3, 4)`.

```cella
maxPlayers: const i32 = 4

Command: enum
{
	holdPosition
	moveTo(x: i32, y: i32)
}
```

## Type and value parameters

- **Type parameters are single uppercase letters:** `T`, `K` and `V` for keys and values, `T` and `E` for a result and its error. A descriptive PascalCase word is fine once letters stop being clear. No `T` prefix (`TKey`).
- **Value parameters are values, so they're camelCase:** `capacity`, not `N`. The built-in array is `array[T, length: const usize]`.

```cella
Ring[T, capacity: const usize]: rec
{
	items: array[T, capacity]
	next: usize
}
```

## Modules, files and projects

- **Module path segments are short lowercase words,** camelCase when a segment needs several: `game.worldGen`, `std.collections`. Prefer a deeper path to a long segment. No underscores or hyphens.
- **Source files are camelCase and named for their main content:** `worldGen.ce`. The file name is not part of the module path. **Module paths should usually follow the folder structure:** a file in `game/worldGen/` declares `mod game.worldGen`. This is a best practice, not a hard rule, since a folder sometimes only groups files.
- **Project names are camelCase,** so the project, its root module and its output share one spelling: `gameServer.celp`, `mod gameServer`, `gameServer.exe`.
- **In the standard library,** the root module `std` declares nothing itself, and `panic` lives in `std.process` beside `exit`, `abort` and `args`.

## Acronyms and numbers

- **Acronyms are capitalized like ordinary words, whatever their length.** The first letter follows the name's own casing: `HttpServer`, `IoError`, `Id`, `Utf8Decoder`, `userId`, `parseJson`, `httpClient`. Never all caps (`HTTPServer`), and no two-letter exception (`IOError`).
- **Numbers join their word with no separator:** `Vec3`, `utf8`, `int64Max`, `sha256`.

## Layout

- **Indent with tabs.**
- **Braces go on their own line,** for declarations and statements alike.
- **A single-statement body drops its braces** and sits on the next line, indented.
- **A body that takes more than one line gets braces,** including one statement wrapped over two lines.
- **If one branch of an `if` / `else if` / `else` chain needs braces, every branch gets them.**
- **A body that is a single expression is written `=> expr`,** for functions, methods, operators, getters, setters and lambdas, actions included. A `match` can be the whole body: `=> match e` with its arms below.
- **A long expression wraps after an operator or after `=>`,** never before: a line can't start with a binary operator, `=` or `=>`.

```cella
clamp: fun(value: i32, low: i32, high: i32) -> i32
{
	if value < low
		ret low
	else if value > high
		ret high

	ret value
}

volume: fun(width: i64, height: i64, depth: i64) -> i64 =>
	width * height * depth

area: fun(shape: Shape) -> f64 => match shape
{
	circle(val r) => 3.14159 * r * r
	rect(val w, val h) => w * h
}

grow: fun(mut counter: Counter) => counter.value += 10
```

## Local bindings

1. **`val` by default.** Use `var` only when the binding is reassigned or changed in place. The compiler gives a hint, "'total' is never changed", on a `var` that nothing changes.
2. **Let the compiler infer types.** Write a type only when the compiler needs it, or when inference would pick the wrong one.
3. **A number that isn't the default type uses the shortest literal suffix that gives it:** `0u` for `u32`, `0u8`, `0i64`. Not `var total: u32 = 0` and not `u32(0)`.

```cella
val enemy = spawn(30)
val count = 4
var total = 0u
for i in 0..count
	total += u32(i)
```

## Imports

- **Name what you use:** `use game.physics.Body`, or a list, `use game.geometry.[Point, area]`.
- **Past about three names from one module, import it whole** with `use game.geometry.*`, as long as the wildcard clashes with nothing.
- **One `use` per module,** sorted alphabetically by path.
- **`use when` lines come first,** right after `mod`.
- A bare module import (`use game.geometry`, then `geometry.area(1, 1)`) and fully qualified paths stay for occasional use.

```cella
mod game.combat

use game.geometry.[Point, area]
use game.physics.Body

strike: fun(target: Point) -> i32 => area(target.x, target.y)
```

## Visibility

| Written as | Top-level declaration | Type member |
| --- | --- | --- |
| `pub` | exported to other projects | n/a |
| nothing | the whole project | same as its type |
| `pvt set` | n/a | readable outside, written only inside |
| `mod` | its module only | its module only |
| `pvt` | its file only | inside the type only |

1. **Use the narrowest level that works.** `pub` only for a library's API. Helpers used in one file are `pvt`. Everything else keeps the default, and `mod` is only for what a module's files share and the rest of the project shouldn't touch.
2. **Fields are open unless the type keeps an invariant on them.** Use `pvt set` when outside code should read a field but not write it, and `pvt` when outside code shouldn't see it. Don't write a property that only returns a private field.
3. **Write the visibility on each declaration.** Use a block (`pvt { … }`) only when about four or more neighbouring declarations share it.
4. **Most visible first, least visible last:** `pub`, no mark, `pvt set`, `mod`, `pvt`. The next section applies this inside each group.

## Ordering

Group by kind first, then put the most visible first within each group, then break remaining ties by how the member takes its instance.

**A file:**

1. `mod`, then `use when`, then `use`
2. `ext` blocks
3. constants and globals
4. types, each followed by its `impl` blocks
5. functions
6. `main`, last

**A type:**

1. constants (constant functions after them, once those exist)
2. static fields
3. static properties and getters
4. fields, in layout order; by visibility only where that costs no space, such as fields of the same size
5. properties and getters
6. constructors
7. `drop`
8. static constructors (`empty`, `withCapacity`)
9. static methods
10. methods
11. operators and conversions

A trait's members go in the group of their kind.

```cella
Buffer: pub rec
{
	maxLength: const i32 = 64

	created: pvt var i32 = 0

	live: get -> i32 => created

	data: pvt ptr[i32]
	length: pvt set i32
	capacity: pvt set i32

	isEmpty: get(imm self) -> bool => self.length == 0

	new: op(capacity: i32)
	{
		self.data = malloc(usize(capacity) * sizeOf(i32))
		self.length = 0
		self.capacity = capacity
		created += 1
	}

	drop: op
	{
		free(self.data)
		created -= 1
	}

	empty: fun -> Buffer => Buffer(0)

	fill: fun(mut self, value: i32)
	{
		for i in 0..self.capacity
			self.data[i] = value

		self.length = self.capacity
	}

	clampIndex: pvt fun(imm self, index: i32) -> i32 => index % self.capacity

	==: op(imm a: Buffer, imm b: Buffer) -> bool => a.length == b.length
}
```

**Receiver mode, the last tie-breaker:** copy (bare `self`), then `imm`, then `mut`, then `own`. A member that only looks at its instance comes before one that changes it, and one that consumes it comes last. Static members have no instance, which is why their groups come first.

- A `prop` with both accessors is placed by its getter's mode.
- An operator is placed by its first operand's mode, as in `==: op(imm a: Buffer, imm b: Buffer)`.

```cella
Inventory: rec
{
	items: ptr[i32]
	count: pvt set i32

	new: op(items: ptr[i32])
	{
		self.items = items
		self.count = 0
	}

	first: fun(self) -> i32 => self.items[0]

	total: fun(imm self) -> i32
	{
		var sum = 0
		for i in 0..self.count
			sum += self.items[i]

		ret sum
	}

	add: fun(mut self, item: i32)
	{
		self.items[self.count] = item
		self.count += 1
	}

	release: fun(own self) -> ptr[i32] => self.items
}
```
