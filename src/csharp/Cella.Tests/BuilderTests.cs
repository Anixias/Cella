using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cella.Compiler;
using Cella.Compiler.Building;
using Cella.Core.Binding;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Diagnostics;
using Xunit;

namespace Cella.Tests;

public sealed class BuilderTests : IDisposable
{
	private const string ValidSource = "mod test\n\nmain: fun -> i32 => 0\n";
	private const string InvalidSource = "mod test\n\nmain: fun -> i32 => true\n";
	
	private const string LibrarySource = """
	                                     mod lib
	                                     
	                                     Shape: pub trait
	                                     {
	                                     	area: fun(imm self) -> i32
	                                     }
	                                     
	                                     box[T]: pub rec
	                                     {
	                                     	value: T
	                                     }
	                                     
	                                     Square: pub rec impl Shape
	                                     {
	                                     	side: i32
	                                     
	                                     	area: fun(imm self) -> i32 => self.side * self.side
	                                     }
	                                     
	                                     total[T: Shape]: pub fun(imm shape: T) -> i32 => shape.area()
	                                     
	                                     """;
	
	private const string ProgramSource = """
	                                     mod test
	                                     
	                                     use lib
	                                     
	                                     Named: trait
	                                     {
	                                     	code: fun(imm self) -> i32
	                                     }
	                                     
	                                     Circle: rec impl lib.Shape
	                                     {
	                                     	radius: i32
	                                     
	                                     	area: fun(imm self) -> i32 => 3 * self.radius * self.radius
	                                     	+: op(imm a: Circle, imm b: Circle) -> Circle => Circle(a.radius + b.radius)
	                                     }
	                                     
	                                     i32: impl Named
	                                     {
	                                     	code: fun(imm self) -> i32 => self
	                                     }
	                                     
	                                     i32: impl
	                                     {
	                                     	twice: fun(imm self) -> i32 => self * 2
	                                     }
	                                     
	                                     main: fun -> i32
	                                     {
	                                     	val held = lib.box[Circle](Circle(2))
	                                     	val sum = Circle(1) + Circle(2)
	                                     	val number: i32 = 4
	                                     	ret lib.total(imm held.value) + lib.total(imm lib.Square(3)) + number.code() + number.twice() + sum.radius
	                                     }
	                                     
	                                     """;
	
	private readonly string _directory =
		Path.Combine(Path.GetTempPath(), "cella-builder-tests", Guid.NewGuid().ToString("N"));
	
	private sealed class RecordingOutput : IBuildOutput
	{
		public List<string> Lines { get; } = [];
		public List<Diagnostic> Diagnostics { get; } = [];
		
		public void WriteLine(string text) => Lines.Add(text);
		public void WriteError(string text) => Lines.Add(text);
		public void Report(IEnumerable<Diagnostic> diagnostics) => Diagnostics.AddRange(diagnostics);
		
		public void ReportOutput(string projectName, string outputPath) => Outputs.Add((projectName, outputPath));
		
		public List<(string Project, string Path)> Outputs { get; } = [];
	}
	
	public BuilderTests() => Directory.CreateDirectory(_directory);
	
	public void Dispose() => Directory.Delete(_directory, true);
	
	private static CancellationToken Token => TestContext.Current.CancellationToken;
	
	private string SourcePath => Path.Combine(_directory, "main.ce");
	private string LibraryPath => Path.Combine(_directory, "lib", "lib.ce");
	private string ProgramPath => Path.Combine(_directory, "app", "main.ce");
	
	private static Builder CreateBuilder(IBuildOutput output) => new(new BuildOptions
	{
		Target = TargetTriple.FromHost().WithDefaultAbi(),
		ChecksOnly = true
	}, output);
	
	private Task<BuildResult> CheckAsync(Builder builder, IReadOnlyDictionary<string, string>? unsavedFiles = null) =>
		CompilerThread.Run(() => builder.BuildAsync(_directory, unsavedFiles, Token));
	
	private async Task<(BuildResult Result, string Output, List<(string Project, string Path)> Outputs)>
		BuildProgramAsync()
	{
		var output = new RecordingOutput();
		var builder = new Builder(new BuildOptions
		{
			Target = TargetTriple.FromHost().WithDefaultAbi()
		}, output);
		
		var result = await CompilerThread.Run(() =>
			builder.BuildAsync(Path.Combine(_directory, "app", "test.celp"), null, Token));
		
		return (result, string.Join('\n', output.Lines.Concat(output.Diagnostics.Select(static d => d.Message))),
			output.Outputs);
	}
	
	[Fact]
	public async Task BuildsUserProjectsNamedStdInPlace()
	{
		Directory.CreateDirectory(Path.Combine(_directory, "std"));
		Directory.CreateDirectory(Path.Combine(_directory, "app"));
		await File.WriteAllTextAsync(Path.Combine(_directory, "std", "std.celp"), OptedOut("StaticLibrary"), Token);
		await File.WriteAllTextAsync(Path.Combine(_directory, "std", "std.ce"),
			"mod mine\n\nvalue: pub fun -> i32 => 0\n", Token);
		
		await File.WriteAllTextAsync(Path.Combine(_directory, "app", "test.celp"),
			OptedOut("Executable") + "\n[[ProjectReferences]]\nPath = '../std/std.celp'\n", Token);
		
		await File.WriteAllTextAsync(ProgramPath, "mod test\n\nuse mine\n\nmain: fun -> i32 => mine.value()\n", Token);
		var (result, output, _) = await BuildProgramAsync();
		Assert.True(result.Succeeded, output);
		Assert.NotEmpty(Directory.GetFiles(Path.Combine(_directory, "std", "bin")));
		Assert.False(Directory.Exists(Path.Combine(_directory, "app", "obj", "std")));
	}
	
	[Theory]
	[InlineData("core")]
	[InlineData("os")]
	public async Task BuildsUserProjectsNamedLikeStandardLibraryProjects(string name)
	{
		Directory.CreateDirectory(Path.Combine(_directory, name));
		Directory.CreateDirectory(Path.Combine(_directory, "app"));
		await File.WriteAllTextAsync(Path.Combine(_directory, name, $"{name}.celp"),
			"OutputType = \"StaticLibrary\"\n", Token);
		
		await File.WriteAllTextAsync(Path.Combine(_directory, name, "lib.ce"),
			"mod mine\n\nvalue: pub fun -> i32 => 0\n", Token);
		
		await File.WriteAllTextAsync(Path.Combine(_directory, "app", "test.celp"),
			$"OutputType = \"Executable\"\n\n[[ProjectReferences]]\nPath = '../{name}/{name}.celp'\n", Token);
		
		await File.WriteAllTextAsync(ProgramPath, "mod test\n\nuse mine\n\nmain: fun -> i32 => mine.value()\n", Token);
		var (result, output, _) = await BuildProgramAsync();
		Assert.True(result.Succeeded, output);
	}
	
	[Fact]
	public async Task BuildsTheStandardLibraryIntoTheProgram()
	{
		Directory.CreateDirectory(Path.Combine(_directory, "app"));
		await File.WriteAllTextAsync(Path.Combine(_directory, "app", "test.celp"), "OutputType = \"Executable\"\n",
			Token);
		
		await File.WriteAllTextAsync(ProgramPath, ValidSource, Token);
		var (result, output, outputs) = await BuildProgramAsync();
		Assert.True(result.Succeeded, output);
		var (project, path) = Assert.Single(outputs);
		Assert.Equal("test", project);
		Assert.Equal(Path.Combine(_directory, "app", "bin"), Path.GetDirectoryName(path));
		var bundled = Directory.GetDirectories(Path.Combine(_directory, "app", "obj", "std"));
		Assert.NotEmpty(bundled);
		Assert.All(bundled, static project => Assert.NotEmpty(Directory.GetFiles(Path.Combine(project, "bin"))));
		Assert.DoesNotContain(
			Directory.EnumerateDirectories(Path.Combine(AppContext.BaseDirectory, "lib", "std"), "*",
				SearchOption.AllDirectories), static directory => Path.GetFileName(directory) is "bin" or "obj");
	}
	
	[Fact]
	public async Task ReportsNothingFromTheStandardLibrary()
	{
		Directory.CreateDirectory(Path.Combine(_directory, "app"));
		await File.WriteAllTextAsync(Path.Combine(_directory, "app", "test.celp"), "OutputType = \"Executable\"\n",
			Token);
		
		await File.WriteAllTextAsync(ProgramPath, "mod test\n\nunused: fun -> i32 => 0\n\nmain: fun -> i32 => 0\n",
			Token);
		
		var output = new RecordingOutput();
		var result = await CheckAsync(CreateBuilder(output));
		var standardLibrary = Path.Combine(AppContext.BaseDirectory, "lib", "std") + Path.DirectorySeparatorChar;
		Assert.True(result.Succeeded);
		var reported = Assert.Single(output.Diagnostics);
		Assert.Equal(ProgramPath, reported.SourceLocation.Source.FilePath);
		var checks = result.Projects.Where(check => check.ProjectPath.StartsWith(standardLibrary,
			StringComparison.OrdinalIgnoreCase)).ToList();
		
		Assert.NotEmpty(checks);
		Assert.All(checks, static check => Assert.Empty(check.Diagnostics));
	}
	
	private static string OptedOut(string outputType) =>
		$"OutputType = \"{outputType}\"\nStandardLibrary = \"None\"\n";
	
	private async Task WriteProgramAsync(string source)
	{
		await File.WriteAllTextAsync(Path.Combine(_directory, "test.celp"), OptedOut("Executable"), Token);
		await File.WriteAllTextAsync(SourcePath, source, Token);
	}
	
	private async Task WriteLibraryProjectsAsync()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath)!);
		Directory.CreateDirectory(Path.GetDirectoryName(ProgramPath)!);
		await File.WriteAllTextAsync(Path.Combine(_directory, "lib", "lib.celp"), OptedOut("StaticLibrary"), Token);
		await File.WriteAllTextAsync(Path.Combine(_directory, "app", "test.celp"),
			OptedOut("Executable") + "\n[[ProjectReferences]]\nPath = '../lib/lib.celp'\n", Token);
		
		await File.WriteAllTextAsync(LibraryPath, LibrarySource, Token);
		await File.WriteAllTextAsync(ProgramPath, ProgramSource, Token);
	}
	
	private static ProjectCheck FindProject(BuildResult result, string directoryName) =>
		Assert.Single(result.Projects,
			project => Path.GetFileName(Path.GetDirectoryName(project.ProjectPath)) == directoryName);
	
	private static int CountPoolEntries(Builder builder) =>
		builder.TypePool!.CreateSnapshot().Collections.Sum(static collection =>
			((IEnumerable)collection).Cast<object>().Count());
	
	private static string Describe(BuildResult result) =>
		string.Join("\n", result.Projects.SelectMany(static project => project.Diagnostics));
	
	[Fact]
	public async Task ChecksWithoutBuilding()
	{
		await WriteProgramAsync(ValidSource);
		var output = new RecordingOutput();
		var result = await CheckAsync(CreateBuilder(output));
		
		Assert.True(result.Succeeded);
		Assert.False(Directory.Exists(Path.Combine(_directory, "bin")));
		var project = Assert.Single(result.Projects);
		Assert.Equal("test.celp", Path.GetFileName(project.ProjectPath));
		Assert.Empty(project.Diagnostics);
		Assert.NotEmpty(project.Files);
		Assert.Empty(output.Lines);
	}
	
	[Fact]
	public async Task ChecksPlainNamesOfContainedLibraries()
	{
		const string settings = "StandardLibrary = \"None\"\nSystemLinkPreference = \"Dynamic\"\n";
		Directory.CreateDirectory(Path.Combine(_directory, "d"));
		Directory.CreateDirectory(Path.Combine(_directory, "s"));
		await File.WriteAllTextAsync(Path.Combine(_directory, "test.celp"),
			$"OutputType = \"Executable\"\n{settings}\n[[ProjectReferences]]\nPath = 'd/d.celp'\n", Token);
		
		await File.WriteAllTextAsync(SourcePath, "mod test\n\nmain: fun -> i32 => d.call()\n", Token);
		await File.WriteAllTextAsync(Path.Combine(_directory, "d", "d.celp"),
			$"OutputType = \"SharedLibrary\"\n{settings}\n[[ProjectReferences]]\nPath = '../s/s.celp'\n", Token);
		
		await File.WriteAllTextAsync(Path.Combine(_directory, "d", "d.ce"),
			"mod d\n\nrun: pub ext fun -> i32 => 2\n\ncall: pub fun -> i32 => s.run()\n", Token);
		
		await File.WriteAllTextAsync(Path.Combine(_directory, "s", "s.celp"),
			$"OutputType = \"StaticLibrary\"\n{settings}", Token);
		
		await File.WriteAllTextAsync(Path.Combine(_directory, "s", "s.ce"),
			"mod s\n\nrun: pub ext fun -> i32 => 1\n", Token);
		
		var result = await CheckAsync(CreateBuilder(new RecordingOutput()));
		
		Assert.False(result.Succeeded);
		var problem = Assert.Single(result.ProjectDiagnostics);
		Assert.Equal("'run' is defined by both 's' and 'd'", problem.Message);
		Assert.Equal(Path.Combine(_directory, "d", "d.celp"), problem.Projects[0], ignoreCase: true);
	}
	
	[Theory]
	[InlineData("sum")]
	[InlineData("choice")]
	[InlineData("block")]
	public async Task ReportsDeepNestingOnce(string shape)
	{
		const int depth = 8000;
		var body = shape switch
		{
			"sum" => $"ret {string.Join(" + ", Enumerable.Repeat("x", depth))}\n",
			"choice" => string.Concat(Enumerable.Range(0, depth).Select(static i => $"if x == {i}\n\tret {i}\nelse ")) +
			            "ret 0\n",
			_ =>
				$"{string.Concat(Enumerable.Repeat("{\n", depth))}{string.Concat(Enumerable.Repeat("}\n", depth))}ret 0\n"
		};
		
		await WriteProgramAsync($"mod test\n\nmain: fun -> i32\n{{\nvar x = 0\n{body}}}\n");
		var builder = CreateBuilder(new RecordingOutput());
		
		var result = await CompilerThread.Run(() => builder.BuildAsync(_directory, null, Token), 4 * 1024 * 1024);
		
		var error = Assert.Single(Assert.Single(result.Projects).Diagnostics);
		Assert.Equal("Nesting is too deep", error.Message);
	}
	
	[Fact]
	public async Task ChecksTraitConstantsOfInstances()
	{
		await WriteProgramAsync("""
		                        mod test
		                        
		                        Integer: trait
		                        {
		                        	bits: const u32
		                        }
		                        
		                        Box[T]: rec
		                        {
		                        	value: T
		                        }
		                        
		                        Box[T]: impl Integer
		                        {
		                        	bits: const u32 = Box[T].bits + 1
		                        }
		                        
		                        show[T: Integer]: fun -> u32 => T.bits
		                        
		                        main: fun -> i32 => i32(show[Box[i32]]())
		                        
		                        """);
		
		var result = await CheckAsync(CreateBuilder(new RecordingOutput()));
		
		var error = Assert.Single(Assert.Single(result.Projects).Diagnostics);
		Assert.Equal("'bits' depends on its own value", error.Message);
		Assert.Equal(15, error.SourceLocation.GetLineColumn().Line);
	}
	
	[Fact]
	public async Task ReportsErrorsThroughOutput()
	{
		await WriteProgramAsync(InvalidSource);
		var output = new RecordingOutput();
		var result = await CheckAsync(CreateBuilder(output));
		
		Assert.False(result.Succeeded);
		var project = Assert.Single(result.Projects);
		var error = Assert.Single(project.Diagnostics);
		Assert.Equal(DiagnosticSeverity.Error, error.Severity);
		Assert.Equal(SourcePath, error.SourceLocation.Source.FilePath);
		Assert.Equal([error], output.Diagnostics);
	}
	
	[Fact]
	public async Task ReadsUnsavedFilesInsteadOfDisk()
	{
		await WriteProgramAsync(ValidSource);
		var result = await CheckAsync(CreateBuilder(new RecordingOutput()),
			new Dictionary<string, string> { [Path.GetFullPath(SourcePath)] = InvalidSource });
		
		Assert.False(result.Succeeded);
		Assert.Single(Assert.Single(result.Projects).Diagnostics);
		Assert.Equal(ValidSource, await File.ReadAllTextAsync(SourcePath, Token));
	}
	
	[Fact]
	public async Task ReusesUnchangedDependencies()
	{
		await WriteLibraryProjectsAsync();
		var builder = CreateBuilder(new RecordingOutput());
		var first = await CheckAsync(builder);
		Assert.True(first.Succeeded, Describe(first));
		var poolSize = CountPoolEntries(builder);
		
		for (var edit = 1; edit <= 3; edit++)
		{
			var next = await CheckAsync(builder, new Dictionary<string, string>
			{
				[Path.GetFullPath(ProgramPath)] = ProgramSource + new string('\n', edit)
			});
			
			Assert.Same(FindProject(first, "lib"), FindProject(next, "lib"));
			Assert.NotSame(FindProject(first, "app"), FindProject(next, "app"));
			Assert.True(next.Succeeded, Describe(next));
			Assert.Equal(poolSize, CountPoolEntries(builder));
		}
	}
	
	[Fact]
	public async Task RechecksChangedDependencies()
	{
		await WriteLibraryProjectsAsync();
		var builder = CreateBuilder(new RecordingOutput());
		var first = await CheckAsync(builder);
		var changed = await CheckAsync(builder, new Dictionary<string, string>
		{
			[Path.GetFullPath(LibraryPath)] = LibrarySource.Replace("total[T: Shape]", "sum[T: Shape]")
		});
		
		Assert.NotSame(FindProject(first, "lib"), FindProject(changed, "lib"));
		Assert.False(changed.Succeeded);
		Assert.Contains(FindProject(changed, "app").Diagnostics,
			static diagnostic => diagnostic.Message.Contains("total"));
		
		var restored = await CheckAsync(builder);
		Assert.True(restored.Succeeded, Describe(restored));
	}
	
	[Fact]
	public void SnapshotsCoverEveryField()
	{
		var typePool = new TypePool(ConversionTable.CreateNative(), new OperatorRegistry(), new SizeTable());
		var missing = new List<string>();
		foreach (var (owner, field) in FindStateFields(typePool))
		{
			var name = $"{owner.GetType().Name}.{field.Name}";
			var value = field.GetValue(owner);
			if (field.IsInitOnly)
			{
				if (IsMutableCollection(value?.GetType() ?? field.FieldType) &&
				    !typePool.CreateSnapshot().Collections.Any(saved => ReferenceEquals(saved, value)))
					missing.Add(name);
				
				continue;
			}
			
			var snapshot = typePool.CreateSnapshot();
			field.SetValue(owner, ChangeValue(field.FieldType, value, name));
			snapshot.Restore();
			if (!Equals(value, field.GetValue(owner)))
				missing.Add(name);
			
			field.SetValue(owner, value);
		}
		
		Assert.Empty(missing);
	}
	
	private static IEnumerable<(object Owner, FieldInfo Field)> FindStateFields(object root)
	{
		var pending = new Queue<object>([root]);
		var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { root };
		while (pending.TryDequeue(out var owner))
		{
			foreach (var field in owner.GetType()
				         .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				yield return (owner, field);
				if (field.GetValue(owner) is { } value && IsStateOwner(value.GetType()) && seen.Add(value))
					pending.Enqueue(value);
			}
		}
	}
	
	private static bool IsStateOwner(Type type) => type.IsClass && type.Assembly == typeof(TypePool).Assembly &&
	                                               !typeof(Symbol).IsAssignableFrom(type) &&
	                                               !typeof(Delegate).IsAssignableFrom(type) &&
	                                               !IsMutableCollection(type);
	
	private static bool IsMutableCollection(Type type) =>
		typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string) &&
		type.Namespace is not ("System.Collections.Immutable" or "System.Collections.Frozen");
	
	private static object? ChangeValue(Type type, object? value, string name) => value switch
	{
		bool flag => !flag,
		int number => number + 1,
		not null when !type.IsValueType => null,
		null when typeof(Delegate).IsAssignableFrom(type) => CreateDelegate(type),
		null when type.IsClass && !type.IsAbstract => RuntimeHelpers.GetUninitializedObject(type),
		_ => throw new InvalidOperationException($"Can't change {name}")
	};
	
	private static Delegate CreateDelegate(Type type)
	{
		var invoke = type.GetMethod(nameof(Action.Invoke))!;
		var parameters = invoke.GetParameters().Select(static p => Expression.Parameter(p.ParameterType)).ToArray();
		return Expression.Lambda(type, Expression.Default(invoke.ReturnType), parameters).Compile();
	}
}