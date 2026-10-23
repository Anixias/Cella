using System.Collections.Immutable;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public readonly record struct InlineReturn(FunctionInfo? Caller, SourceLocation Site, TypeSymbol? Operand = null);
public readonly record struct ValueParameterUse(bool IsArithmetic, string Hint);

public sealed class InlineDeclaration(ResolutionContext context)
{
	public ResolutionContext Context { get; } = context;
}

public readonly struct ResolutionContext
{
	public FileSymbol File { get; init; }
	public TypeSymbol? ContainingType { get; init; }
	public FunctionInfo? ContainingFunction { get; init; }
	public ImportEnvironment? Imports { get; init; }
	public ModuleIndex? Modules { get; init; }
	public Scope? LocalScope { get; init; }
	public TypePool TypePool { get; init; }
	public DiagnosticList Diagnostics { get; init; }
	public ExtSignatureTypes? ExtSignatureTypes { get; init; }
	public ErrorContainment? Containment { get; init; }
	public Func<IExpressionNode, ResolutionContext, TypeSymbol?, Constant?>? EvaluateConstant { get; init; }
	public Func<IExpressionNode, ResolutionContext, ValueParameterUse?>? FindValueParameterUse { get; init; }
	public Dictionary<IndexerExpressionNode, TypeSymbol?>? GenericTypes { get; init; }
	public Action<Action>? RecordUndo { get; init; }
	public ImmutableArray<TypeParameterSymbol> TypeParameters { get; init; }
	public ImmutableArray<TypeParameterSymbol> ExcludedParameters { get; init; }
	public string? ExclusionPlace { get; init; }
	public TraitSymbol? Trait { get; init; }
	public ImplSymbol? ImplBlock { get; init; }
	public TypeSymbol? TraitSelf { get; init; }
	public CompletionPlace? TraitPlace { get; init; }
	public Func<string, Symbol?>? ResolveSiblingMember { get; init; }
	public Func<ConstraintCheck, bool>? DeferConstraintCheck { get; init; }
	public Func<string, Symbol?>? Capture { get; init; }
	public Action<ResolutionContext, TypeSymbol, SourceLocation>? TrackInstance { get; init; }
	public Action<Token, Symbol>? RecordName { get; init; }
	public Action<LocalVariableSymbol>? MarkUsed { get; init; }
	public CompletionProbe? Probe { get; init; }
	public InlineReturn? InlineReturn { get; init; }
	
	public string Mangle(Symbol symbol) => Mangling.Mangle(symbol, Modules, GetQualifiers());
	
	public (string Name, string Base) Mangle(Symbol symbol, FunctionSignature signature) =>
		Mangling.Mangle(symbol, signature, Modules, GetQualifiers());
	
	public ImmutableArray<NamePart> Qualify(FunctionSymbol function) =>
	[
		..GetModuleParts().Select(static part => new NamePart(part, [])),
		..GetContainers().Select(static container => container switch
		{
			FunctionSymbol f => new NamePart(f.DeclaredName, [..f.DeclaredTypeParameters]),
			NamedTypeSymbol t => new NamePart(t.Definition.Name, t.TypeArguments),
			TraitSymbol t => new NamePart(t.Name, [..t.TypeParameters]),
			ImplSymbol i => i.Traits is [var trait]
				? new NamePart(trait.Trait.Name, trait.Arguments)
				: new NamePart("", [])
					{ Combined = [..i.Traits.Select(static t => new NamePart(t.Trait.Name, t.Arguments))] },
			_ => new NamePart(container.Name, [])
		}),
		new(function.DeclaredName, [..function.DeclaredTypeParameters])
	];
	
	private List<string> GetQualifiers()
	{
		var modules = Modules;
		return [..GetModuleParts(), ..GetContainers().Select(container => Mangling.MangleName(container, modules))];
	}
	
	private IEnumerable<string> GetModuleParts() => File.Module.ModuleName.Parts.Select(static part => part.Text);
	
	private List<Symbol> GetContainers()
	{
		var result = new List<Symbol>();
		
		for (var f = ContainingFunction; f is not null; f = f.Value.Symbol.ContainingFunction)
			result.Add(f.Value.Symbol);
		
		if (ImplBlock is { Node.Traits.IsEmpty: false } impl)
			result.Add(impl);
		
		for (var t = ContainingType; t is not null; t = t.ContainingType)
			result.Add(Trait is { } trait && t == trait.Self ? trait : t);
		
		result.Reverse();
		return result;
	}
	
	private static Symbol? ResolveStatic(TypeSymbol type, string name) => ResolveFrom(name,
	[
		..type.GetFunctions(name)
			.Where(static function => !function.HasReceiver && !function.Function.IsConversion)
			.Select(static function => function.Function)
	]);
	
	public static Symbol? ResolveStatic(IEnumerable<MethodSymbol> functions, IEnumerable<PropertySymbol> properties,
		string name) =>
		properties.FirstOrDefault(p => p.Name == name && p.IsStatic) ?? ResolveFrom(name,
			[..GetStatics(functions.Where(function => function.Name == name))]);
	
	private static IEnumerable<Symbol> GetStatics(IEnumerable<MethodSymbol> functions,
		IEnumerable<PropertySymbol> properties) =>
		properties.Where(static property => property.IsStatic).Concat<Symbol>(GetStatics(functions));
	
	private static IEnumerable<FunctionSymbol> GetStatics(IEnumerable<MethodSymbol> functions) => functions
		.Where(static function => !function.HasReceiver && !function.Function.IsConversion)
		.Select(static function => function.Function);
	
	private static IEnumerable<Symbol> GetStatics(TypeSymbol type) => type switch
	{
		RecordSymbol record => GetStatics(record.Members.OfType<MethodSymbol>(), record.Properties)
			.Concat(record.StaticFields),
		EnumSymbol enumType => GetStatics(enumType.Functions, enumType.Properties).Concat(enumType.StaticFields),
		NamedTypeSymbol named => GetStatics([], named.Properties).Concat(named.StaticFields),
		_ => []
	};
	
	private static Symbol? ResolveFrom(string name, IReadOnlyCollection<Symbol> candidates) => candidates switch
	{
		{ Count: > 1 } => new AmbiguousSymbol(name, candidates),
		{ Count: 1 } => candidates.First(),
		_ => null
	};
	
	private const string RootModuleName = "mod";
	
	public Symbol? Resolve(Token name) => Record(name, Resolve(name.Text));
	
	public Symbol? ResolveAll(Token name) => Record(name, ResolveAll(name.Text));
	
	private Symbol? Record(Token name, Symbol? symbol)
	{
		if (symbol is TypeSymbol or TraitSymbol or ModulePathSymbol)
			RecordName?.Invoke(name, symbol);
		
		return symbol;
	}
	
	public Symbol? Resolve(string name, Func<TypeSymbol, string, Symbol?>? findMember = null) =>
		TypeOverloads.ChooseBare(ResolveAll(name, findMember));
	
	public Symbol? ResolveAll(string name, Func<TypeSymbol, string, Symbol?>? findMember = null)
	{
		if (NativeSymbols.IsBuiltInTypeName(name))
			return NativeSymbols.Resolve(name);
		
		if (ResolveScoped(name, findMember) is { } scoped)
			return IsExcluded(scoped) ? null : scoped;
		
		List<Symbol> module = [..GetModuleSymbols(name).Where(IsVisible)];
		if (Imports is { } environment && environment.Resolve(name) is { Length: > 0 } imports)
		{
			List<Symbol> kept =
				[..imports.Where(import => !module.Contains(import) && IsKeptBeside(environment, import, module))];
			
			foreach (var import in kept)
				environment.MarkResolved(import);
			
			return ResolveFrom(name, [..module, ..kept]);
		}
		
		if (module.Count > 0)
			return ResolveFrom(name, module);
		
		if (name == RootModuleName)
			return Modules?.Root;
		
		return NativeSymbols.Resolve(name) ??
		       Modules?.Find(File.Module.ModuleName.Text)?.Children.GetValueOrDefault(name) ??
		       Modules?.Root.Children.GetValueOrDefault(name);
	}
	
	private bool IsExcluded(Symbol symbol) =>
		symbol is TypeParameterSymbol parameter && !ExcludedParameters.IsDefault &&
		ExcludedParameters.Contains(parameter);
	
	private static bool IsKeptBeside(ImportEnvironment imports, Symbol import, List<Symbol> module) =>
		module.Count == 0 || imports.IsExplicit(import) ||
		import is FunctionSymbol && module.All(static symbol => symbol is FunctionSymbol) ||
		module.All(symbol => TypeOverloads.CanOverload(import, symbol) && !TypeOverloads.Overlap(import, symbol));
	
	public Symbol? ResolveNear(string name) =>
		ResolveScoped(name) ?? ResolveFrom(name, [..GetModuleSymbols(name).Where(IsVisible)]);
	
	private Symbol? ResolveScoped(string name, Func<TypeSymbol, string, Symbol?>? findMember = null)
	{
		if (LocalScope?.Resolve(name) is { } localSymbol)
			return localSymbol;
		
		if (ContainingFunction?.Symbol.Parameters.FirstOrDefault(p => p.Name == name) is { } param)
			return param;
		
		if (Capture?.Invoke(name) is { } captured)
			return captured;
		
		if (!TypeParameters.IsDefault && TypeParameters.FirstOrDefault(p => p.Name == name) is { } typeParameter)
			return typeParameter;
		
		if (Trait is { } trait && name == trait.Self.Name)
			return trait.Self;
		
		if (Trait?.TypeParameters.FirstOrDefault(p => p.Name == name) is { } traitParameter)
			return traitParameter;
		
		if (ImplBlock is { } block && block.TypeParameters.FirstOrDefault(p => p.Name == name && block.Declares(p)) is
			    { } blockParameter)
			return blockParameter;
		
		if (ImplBlock is { } impl && ResolveStatic(impl.Functions, impl.Properties, name) is { } blockMember)
			return blockMember;
		
		if (ImplBlock?.Constants.FirstOrDefault(constant => constant.Name == name) is { } blockConstant)
			return blockConstant;
		
		if (ResolveSiblingMember?.Invoke(name) is { } siblingMember)
			return siblingMember;
		
		if (Trait is { } owner && ResolveStatic(owner.Functions, owner.Properties, name) is { } traitMember)
			return traitMember;
		
		if (Trait?.GetConstant(name) is { } traitConstant)
			return traitConstant;
		
		for (var type = ContainingType; type is not null; type = type.ContainingType)
		{
			if (type.Children.TryGetValue(name, out var member))
				return member;
			
			if (type.GetStaticField(name) is { } field)
				return field;
			
			if (type.GetProperty(name) is { IsStatic: true } property)
				return property;
			
			if (ResolveStatic(type, name) is { } function)
				return function;
			
			if (findMember?.Invoke(type, name) is { } found)
				return found;
		}
		
		return null;
	}
	
	private IEnumerable<Symbol> GetModuleSymbols(string name) =>
		File.Module.Files.SelectMany(file => file.Symbols.GetValueOrDefault(name) ?? []);
	
	public bool IsVisible(Symbol symbol) => Modules?.IsVisible(symbol, File) ?? true;
	
	public bool IsReachable(TraitSymbol trait) => Modules?.IsReachable(trait) ?? true;
	
	public bool CanAccess(TypeSymbol owner, Visibility visibility) =>
		Modules?.IsAccessible(owner, visibility, File, ContainingType) ?? true;
	
	public bool IsInModuleOf(Symbol symbol) => Modules is null || Modules.FindModule(symbol) == File.Module;
	
	public IEnumerable<FunctionSymbol> FindHiddenFunctions(string name)
	{
		var modules = Modules;
		var file = File;
		return GetModuleSymbols(name).Concat(Imports?.ResolveHidden(name) ?? []).OfType<FunctionSymbol>()
			.Where(function => modules is not null && !modules.IsVisible(function, file));
	}
	
	public Diagnostic? ReportHidden(string name, SourceLocation location)
	{
		if (!ExcludedParameters.IsDefault &&
		    ExcludedParameters.FirstOrDefault(parameter => parameter.Name == name) is { } excluded)
			return new(DiagnosticSeverity.Error, location,
				$"Cannot use {(excluded.IsValue ? "value" : "type")} parameters in {ExclusionPlace}");
		
		foreach (var symbol in GetModuleSymbols(name).Concat(Imports?.ResolveHidden(name) ?? []))
		{
			if (!IsVisible(symbol))
				return DiagnosticReporter.ReportHidden(location, name, ModuleIndex.GetVisibility(symbol), false);
		}
		
		return null;
	}
	
	public Symbol? ResolveMember(ModulePathSymbol module, string name) =>
		TypeOverloads.ChooseBare(ResolveAllMembers(module, name));
	
	public Symbol? ResolveAllMembers(ModulePathSymbol module, string name) =>
		module.Members.TryGetValue(name, out var members) &&
		ResolveFrom(name, [..members.Where(IsVisible)]) is { } member
			? member
			: module.Children.GetValueOrDefault(name);
	
	public IEnumerable<FunctionSymbol> FindHiddenMembers(ModulePathSymbol module, string name)
	{
		var context = this;
		return module.Members.TryGetValue(name, out var members)
			? members.OfType<FunctionSymbol>().Where(function => !context.IsVisible(function))
			: [];
	}
	
	public Diagnostic ReportUndefinedMember(SourceLocation location, ModulePathSymbol module, string name)
	{
		if (module.Members.TryGetValue(name, out var members) && members.Count > 0)
			return DiagnosticReporter.ReportHidden(location, $"{module.Path}.{name}",
				members.Max(ModuleIndex.GetVisibility), false);
		
		var memberNames = new List<string>();
		foreach (var (memberName, symbols) in module.Members)
		{
			if (symbols.Any(IsVisible))
				memberNames.Add(memberName);
		}
		
		return DiagnosticReporter.ReportUndefinedMember(location, module, name, memberNames);
	}
	
	public ModulePathSymbol? ResolveModule(IExpressionNode node) => node switch
	{
		VarExpressionNode v => Resolve(v.Identifier) as ModulePathSymbol,
		AccessExpressionNode a => ResolveModule(a.Target) is { } module
			? Record(a.Member, ResolveMember(module, a.Member.Text)) as ModulePathSymbol
			: null,
		_ => null
	};
	
	public TraitType? ResolveTrait(ITypeNode node)
	{
		var text = node.SourceLocation.GetText().ToString();
		var location = node.SourceLocation;
		ImmutableArray<IGenericArgumentNode> arguments = [];
		Symbol? symbol;
		switch (node)
		{
			case IdentifierTypeNode identifier:
				ProbeTrait(identifier.Token);
				symbol = Resolve(identifier.Token);
				break;
			
			case QualifiedTypeNode qualified:
				symbol = ResolveQualifiedName(qualified.Parts);
				if (symbol is null)
					return null;
				
				break;
			
			case GenericTypeNode generic:
				ImmutableArray<Token> parts = [..generic.Qualifiers, generic.Identifier];
				text = string.Join('.', parts.Select(static part => part.Text));
				location = GetSpan(parts);
				if (generic.Qualifiers.IsEmpty)
					ProbeTrait(generic.Identifier);
				
				symbol = generic.Qualifiers.IsEmpty
					? ResolveAll(generic.Identifier)
					: ResolveQualifiedName(parts, true);
				
				if (symbol is null && !generic.Qualifiers.IsEmpty)
					return null;
				
				arguments = generic.Arguments;
				if (!TryChooseOverload(ref symbol, arguments, text, location))
					return null;
				
				Record(generic.Identifier, symbol);
				break;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{text}' is not a trait"));
				return null;
		}
		
		switch (symbol)
		{
			case TraitSymbol trait:
				if (node is GenericTypeNode emptyGeneric)
					ReportRedundantBrackets(emptyGeneric, trait.TypeParameters);
				
				var inner = TraitSelf is null ? this : this with { TraitSelf = null };
				return InstantiateTrait(trait,
					ResolveGenericArguments(arguments, trait.TypeParameters, inner.ResolveGenericTypeArgument),
					[..arguments.Select(static argument => argument.SourceLocation)], node.SourceLocation);
			
			case null:
				Diagnostics.Add(ReportHidden(text, location) ?? WithImportFixes(DiagnosticReporter.WithSuggestion(
						new(DiagnosticSeverity.Error, location, $"Trait '{text}' not found in this scope"),
						DiagnosticReporter.FindBestMatch(text,
							GetAllSymbols().OfType<TraitSymbol>().Select(static trait => trait.Name))), text,
					static symbol => symbol is TraitSymbol));
				
				return null;
			
			case AmbiguousSymbol ambiguous:
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, TypeOverloads.FindFamily(ambiguous) switch
				{
					[TraitSymbol, ..] => $"'{text}' needs generic arguments",
					[NamedTypeSymbol, ..] => $"'{text}' is not a trait",
					_ => $"'{text}' is ambiguous"
				}));
				
				return null;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"'{text}' is not a trait"));
				return null;
		}
	}
	
	public static string DescribeMissingOverload(string name, int count) => count == 0
		? $"'{name}' needs generic arguments"
		: $"No '{name}' takes {DescribeGenericArguments(count)}";
	
	private static string DescribeAmbiguousType(AmbiguousSymbol symbol, string? written = null) =>
		TypeOverloads.FindFamily(symbol) switch
		{
			[NamedTypeSymbol, ..] => $"'{written ?? symbol.Name}' needs generic arguments",
			[TraitSymbol, ..] => $"'{written ?? symbol.Name}' is not a type",
			_ => $"'{written ?? symbol.Name}' is ambiguous"
		};
	
	private static Token? FindNamedToken(IExpressionNode name) => name switch
	{
		VarExpressionNode variable => variable.Identifier,
		AccessExpressionNode access => access.Member,
		_ => null
	};
	
	private bool TryChooseOverload(ref Symbol? symbol, IReadOnlyList<IGenericArgumentNode> arguments, string name,
		SourceLocation location)
	{
		if (TypeOverloads.FindFamily(symbol) is not { } family)
			return true;
		
		List<Symbol> counted = [..family.Where(member => TypeOverloads.AcceptsCount(member, arguments.Count))];
		var fitting = counted;
		if (counted.Count > 1)
		{
			List<GenericArgumentKind?> kinds = [..arguments.Select(ClassifyArgument)];
			fitting = kinds.Contains(null)
				? [counted[0]]
				: [..counted.Where(member => TypeOverloads.Accepts(member, kinds))];
		}
		
		if (fitting is [var chosen])
		{
			symbol = chosen;
			return true;
		}
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, location, counted.Count == 0
			? DescribeMissingOverload(name, arguments.Count)
			: fitting.Count == 0
				? $"No '{name}' accepts these generic arguments"
				: $"'{name}' is ambiguous"));
		
		return false;
	}
	
	private GenericArgumentKind? ClassifyArgument(IGenericArgumentNode node) => node switch
	{
		TypeArgumentNode { Type: IdentifierTypeNode type } => ClassifyName(Resolve(type.Token.Text)),
		TypeArgumentNode { Type: QualifiedTypeNode type } => ClassifyName(FindQualifiedQuietly(type.Parts)),
		TypeArgumentNode { Type: GenericTypeNode type } =>
			ClassifyName(FindQualifiedQuietly([..type.Qualifiers, type.Identifier])) == GenericArgumentKind.Trait
				? GenericArgumentKind.Trait
				: GenericArgumentKind.Type,
		TypeArgumentNode => GenericArgumentKind.Type,
		IdentifierArgumentNode argument => ClassifyName(Resolve(argument.Identifier.Text)),
		ExpressionArgumentNode argument => ClassifyExpression(argument.Expression),
		_ => null
	};
	
	private GenericArgumentKind? ClassifyExpression(IExpressionNode expression) => expression switch
	{
		VarExpressionNode variable => ClassifyName(Resolve(variable.Identifier.Text)),
		IndexerExpressionNode indexer =>
			ClassifyExpression(indexer.Target) == GenericArgumentKind.Trait
				? GenericArgumentKind.Trait
				: GenericArgumentKind.Type,
		AccessExpressionNode access when FindQualifiedQuietly(access) is { } member => ClassifyName(member),
		TypeExpressionNode or BorrowExpressionNode => GenericArgumentKind.Type,
		_ => GenericArgumentKind.Value
	};
	
	private static GenericArgumentKind? ClassifyName(Symbol? symbol) => symbol switch
	{
		TraitSymbol or TypeParameterSymbol { IsTrait: true } => GenericArgumentKind.Trait,
		TypeParameterSymbol { IsValue: true } or VariableSymbol => GenericArgumentKind.Value,
		TypeSymbol => GenericArgumentKind.Type,
		AmbiguousSymbol when TypeOverloads.FindFamily(symbol) is [TraitSymbol, ..] => GenericArgumentKind.Trait,
		AmbiguousSymbol when TypeOverloads.FindFamily(symbol) is not null => GenericArgumentKind.Type,
		_ => null
	};
	
	private Symbol? FindQualifiedQuietly(ImmutableArray<Token> parts)
	{
		var symbol = Resolve(parts[0].Text);
		foreach (var part in parts.Skip(1))
			symbol = symbol is ModulePathSymbol module ? ResolveAllMembers(module, part.Text) : null;
		
		return symbol;
	}
	
	private Symbol? FindQualifiedQuietly(AccessExpressionNode access) => access.Target switch
	{
		VarExpressionNode root => Resolve(root.Identifier.Text) is ModulePathSymbol module
			? ResolveAllMembers(module, access.Member.Text)
			: null,
		AccessExpressionNode inner => FindQualifiedQuietly(inner) is ModulePathSymbol module
			? ResolveAllMembers(module, access.Member.Text)
			: null,
		_ => null
	};
	
	public TraitType? InstantiateTrait(TraitSymbol trait, IReadOnlyList<TypeSymbol> arguments,
		IReadOnlyList<SourceLocation> locations, SourceLocation location)
	{
		var self = new Dictionary<TypeParameterSymbol, TypeSymbol>
			{ [trait.Self] = TraitSelf ?? NativeSymbols.Invalid };
		
		if (TypePool.CompleteArguments(trait.TypeParameters, arguments, self) is { } filled && TraitSelf is null &&
		    FindSelfDefault(trait, arguments.Count, filled) is { } parameter)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"'{trait.Name}' needs an argument for '{parameter.Name}'"));
			
			return null;
		}
		
		return CheckGenericArguments(trait.Name, trait.TypeParameters, arguments, locations, location, self) is
			{ } completed && !completed.Any(static argument => argument is InvalidType)
			? TypePool.GetTraitType(trait, completed)
			: null;
	}
	
	private TypeParameterSymbol? FindSelfDefault(TraitSymbol trait, int written, ImmutableArray<TypeSymbol> filled)
	{
		for (var i = written; i < filled.Length; i++)
			if (TypePool.ContainsInvalid(filled[i]) &&
			    TypePool.GetDefaultArgument(trait.TypeParameters[i]) is { } fallback &&
			    !TypePool.ContainsInvalid(fallback))
				return trait.TypeParameters[i];
		
		return null;
	}
	
	public TypeSymbol? ResolveTraitReference(ITypeNode node) => node switch
	{
		FunctionTypeNode function => ResolveFunctionTrait(function),
		IdentifierTypeNode identifier
			when Resolve(identifier.Token) is TypeParameterSymbol { IsTrait: true } trait => trait,
		_ => ResolveTrait(node)
	};
	
	private FunctionType? ResolveFunctionTrait(FunctionTypeNode node)
	{
		switch (ResolveType(node))
		{
			case FunctionType function when TypePool.IsFunctionTrait(function):
				return function;
			
			case FunctionType function:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
					$"'{function.Name}' is not a trait"));
				
				return null;
			
			default:
				return null;
		}
	}
	
	public Symbol? ResolveTypeName(ITypeNode node, bool keepsOverloads = false) => node switch
	{
		IdentifierTypeNode identifier => keepsOverloads ? ResolveAll(identifier.Token) : Resolve(identifier.Token),
		QualifiedTypeNode qualified => ResolveQualifiedName(qualified.Parts, keepsOverloads),
		_ => null
	};
	
	public Symbol? ResolveQualifiedName(ImmutableArray<Token> parts, bool keepsOverloads = false)
	{
		var first = parts[0];
		if ((keepsOverloads && parts.Length == 1 ? ResolveAll(first) : Resolve(first)) is { } symbol)
			return ResolveMembers(symbol, parts, 1, keepsOverloads);
		
		Diagnostics.Add(DiagnosticReporter.ReportUndefinedModule(first.SourceLocation, first.Text,
			GetAllSymbols().OfType<ModulePathSymbol>().Select(static m => m.Name).Distinct()));
		
		return null;
	}
	
	public Symbol? ResolveMembers(Symbol symbol, ImmutableArray<Token> parts, int start, bool keepsOverloads = false)
	{
		for (var i = start; i < parts.Length; i++)
		{
			if (symbol is not ModulePathSymbol module)
			{
				Diagnostics.Add(ReportNotModule(symbol, parts[..i]));
				return null;
			}
			
			var found = keepsOverloads && i == parts.Length - 1
				? ResolveAllMembers(module, parts[i].Text)
				: ResolveMember(module, parts[i].Text);
			
			if (Record(parts[i], found) is not { } member)
			{
				Diagnostics.Add(ReportUndefinedMember(parts[i].SourceLocation, module, parts[i].Text));
				return null;
			}
			
			symbol = member;
		}
		
		return symbol;
	}
	
	public static Diagnostic ReportNotModule(Symbol symbol, ImmutableArray<Token> path)
	{
		var name = string.Join('.', path.Select(static p => p.Text));
		var problem = symbol is AmbiguousSymbol ? "is ambiguous" : "is not a module";
		return new(DiagnosticSeverity.Error, GetSpan(path), $"'{name}' {problem}");
	}
	
	public static SourceLocation GetSpan(ImmutableArray<Token> parts)
	{
		var (source, range) = parts[0].SourceLocation;
		return new(source, range.Join(parts[^1].SourceLocation.Range));
	}
	
	public IEnumerable<Symbol> GetAllSymbols()
	{
		for (var scope = LocalScope; scope is not null; scope = scope.Parent)
			foreach (var symbol in scope.Symbols)
				yield return symbol;
		
		if (ContainingFunction is { } function)
			foreach (var param in function.Symbol.Parameters)
				yield return param;
		
		if (!TypeParameters.IsDefault)
			foreach (var typeParameter in TypeParameters)
				yield return typeParameter;
		
		if (Trait is { } trait)
		{
			yield return trait.Self;
			foreach (var symbol in trait.TypeParameters)
				yield return symbol;
		}
		
		if (ImplBlock is { } impl)
			foreach (var symbol in impl.TypeParameters.Where(impl.Declares)
				         .Concat(GetStatics(impl.Functions, impl.Properties)).Concat(impl.Constants))
				yield return symbol;
		
		if (Trait is { } owner)
			foreach (var symbol in GetStatics(owner.Functions, owner.Properties).Concat(owner.Constants))
				yield return symbol;
		
		for (var type = ContainingType; type is not null; type = type.ContainingType)
			foreach (var symbol in type.Children.Values.Concat(GetStatics(type)))
				yield return symbol;
		
		foreach (var file in File.Module.Files)
			foreach (var fileSet in file.Symbols.Values)
				foreach (var fileSymbol in fileSet)
					if (IsVisible(fileSymbol))
						yield return fileSymbol;
		
		if (Imports is { } imports)
			foreach (var import in imports.ImportedSymbols)
				yield return import;
		
		foreach (var primitive in NativeSymbols.PrimitiveTypes)
			yield return primitive;
		
		if (Modules is { } modules)
			foreach (var module in modules.Root.Children.Values)
				yield return module;
	}
	
	public TypeSymbol ResolveReturnType(ITypeNode node) =>
		node is IdentifierTypeNode { Token.Text: "never" } && Resolve("never") is null
			? NativeSymbols.Never
			: ResolveType(node);
	
	public TypeSymbol ResolveType(ITypeNode node)
	{
		if (NameOf(node) is { } probed)
			ProbeType(probed);
		
		var type = ResolveTypeNode(node);
		if (NameOf(node) is { } name)
			Record(name, type);
		
		return type;
	}
	
	private static Token? NameOf(ITypeNode node) => node switch
	{
		IdentifierTypeNode identifier => identifier.Token,
		GenericTypeNode generic => generic.Identifier,
		QualifiedTypeNode qualified => qualified.Parts[^1],
		_ => null
	};
	
	private TypeSymbol ResolveTypeNode(ITypeNode node) => node switch
	{
		IdentifierTypeNode n => ResolveNamedType(n.Token),
		QualifiedTypeNode n => ResolveQualifiedType(n),
		GenericTypeNode n => ResolveGenericType(n),
		FunctionTypeNode n => ResolveFunctionType(n),
		BorrowTypeNode n => ResolveBorrowType(ResolveType(n.Target), n.IsMutable),
		DynTypeNode { Trait: FunctionTypeNode } n => ReportFunctionDyn(n),
		DynTypeNode n => ResolveTraitReference(n.Trait) switch
		{
			TraitType trait => TypePool.GetDynType(trait),
			TypeParameterSymbol trait => TypePool.GetDynType(trait),
			_ => NativeSymbols.Invalid
		},
		_ => NativeSymbols.Invalid
	};
	
	private TypeSymbol ResolveBorrowType(TypeSymbol target, bool isMutable) =>
		target is InvalidType ? target : TypePool.GetBorrowType(target, isMutable);
	
	private TypeSymbol ReportFunctionDyn(DynTypeNode node)
	{
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, "Cannot use 'dyn' with function types"));
		return NativeSymbols.Invalid;
	}
	
	private FunctionType ResolveFunctionType(FunctionTypeNode node)
	{
		var modes = node.ParameterModes.Select(SymbolCollector.GetMode).ToArray();
		var parameterTypes = new TypeSymbol[node.ParameterTypes.Length];
		for (var i = 0; i < parameterTypes.Length; i++)
		{
			var typeNode = node.ParameterTypes[i];
			if (typeNode is BorrowTypeNode borrow)
			{
				Diagnostics.Add(DiagnosticReporter.ReportBorrowParameter(borrow, null,
					node.ParameterModes[i] is not null, node.IsExternal));
				
				modes[i] = borrow.IsMutable ? ParameterMode.Mut : ParameterMode.Imm;
				typeNode = borrow.Target;
			}
			else if (typeNode is FunctionTypeNode { IsRef: true } function)
				Diagnostics.Add(DiagnosticReporter.ReportRefFunctionParameter(function, null,
					node.ParameterModes[i] is not null, node.IsExternal));
			
			parameterTypes[i] = TypePool.GetPassedType(ResolveType(typeNode), modes[i]);
		}
		
		var returnType = node.ReturnType is { } returnTypeNode ? ResolveReturnType(returnTypeNode) : NativeSymbols.Void;
		var functionType = TypePool.GetFunctionType(node.IsExternal, parameterTypes, modes, returnType, node.IsRef,
			node.Variadic is not null, TakesVariadicParameter(node, parameterTypes));
		
		if (functionType.IsExternal && ExtSignatureTypes is { } signatureTypes)
		{
			for (var i = 0; i < node.ParameterTypes.Length; i++)
			{
				if (node.Ellipses[i] is null &&
				    node.ParameterTypes[i] is not (BorrowTypeNode or FunctionTypeNode { IsRef: true }))
					signatureTypes.Add(functionType.GetDeclaredType(i), node.ParameterTypes[i],
						ExtSignatureTypes.PassesByValue(modes[i]));
			}
			
			if (node.ReturnType is not null)
				signatureTypes.Add(returnType, node.ReturnType, true);
		}
		
		return functionType;
	}
	
	private bool TakesVariadicParameter(FunctionTypeNode node, TypeSymbol[] types)
	{
		var takesVariadic = false;
		for (var i = 0; i < node.Ellipses.Length; i++)
		{
			if (node.Ellipses[i] is not { } ellipsis)
				continue;
			
			if (node.IsExternal)
			{
				var (source, range) = ellipsis.SourceLocation;
				Diagnostics.Add(new(DiagnosticSeverity.Error,
					new(source, range.Join(node.ParameterTypes[i].SourceLocation.Range)),
					"Cannot take typed variadic parameters in 'ext' functions")
				{
					Hints = i > 0 && i == node.Ellipses.Length - 1 && node.Variadic is null
						? ["Did you mean '...'?"]
						: []
				});
			}
			else if (node.ParameterModes[i] is { } mode)
				Diagnostics.Add(new(DiagnosticSeverity.Error, mode.SourceLocation,
					$"Cannot take '{mode.Text}' variadic parameters"));
			
			if (i + 1 < node.ParameterTypes.Length)
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.ParameterTypes[i + 1].SourceLocation,
					"Cannot take parameters after variadic parameters"));
			else if (!node.IsExternal && node.ParameterModes[i] is null)
				takesVariadic = ReportVariadicType(types[i], node.ParameterTypes[i]);
		}
		
		return takesVariadic;
	}
	
	private bool ReportVariadicType(TypeSymbol type, ITypeNode node)
	{
		if (type is InvalidType)
			return false;
		
		var message = TypePool.FindRunElements(type).Count switch
		{
			0 => $"'{type.Name}' doesn't meet 'new(ptr[T], usize)'",
			1 => null,
			_ => $"'{type.Name}' meets 'new(ptr[T], usize)' more than once"
		};
		
		if (message is null)
			return true;
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, message)
		{
			Hints = TypePool.DescribeHiddenRun(type) is { } hint ? [hint] : []
		});
		
		return false;
	}
	
	private void ProbeType(Token name)
	{
		if (Probe?.Covers(name) == true)
			Probe.Capture(CompletionPlace.Type, GetAllSymbols().Distinct()
					.Where(static symbol => symbol is TypeSymbol and not TypeParameterSymbol { IsValue: true }
						or TraitSymbol
						or ModulePathSymbol)
					.Select(static symbol => new CompletionCandidate(symbol)), null, File.Syntax,
				GetImportableSymbols()
					.Where(static entry => entry.Symbol is NamedTypeSymbol or TraitSymbol)
					.Select(static entry => new CompletionCandidate(entry.Symbol, Import: entry.Import)));
	}
	
	private void ProbeTrait(Token name)
	{
		if (Probe?.Covers(name) == true)
			Probe.Capture(TraitPlace ?? CompletionPlace.Trait, GetAllSymbols().Distinct()
					.Where(static symbol =>
						symbol is TraitSymbol or TypeParameterSymbol { IsTrait: true } or ModulePathSymbol)
					.Select(static symbol => new CompletionCandidate(symbol)), null, File.Syntax,
				GetImportableSymbols()
					.Where(static entry => entry.Symbol is TraitSymbol)
					.Select(static entry => new CompletionCandidate(entry.Symbol, Import: entry.Import)));
	}
	
	public IEnumerable<(Symbol Symbol, CompletionImport Import)> GetImportableSymbols()
	{
		if (Modules is not { } modules)
			yield break;
		
		var visible = GetAllSymbols().ToHashSet();
		var functionNames = visible.OfType<FunctionSymbol>().Select(static function => function.DeclaredName)
			.ToHashSet();
		
		var otherNames = visible.Where(static symbol => symbol is not FunctionSymbol)
			.Select(static symbol => symbol.Name)
			.ToHashSet();
		
		var pending = new Stack<ModulePathSymbol>([modules.Root]);
		while (pending.TryPop(out var module))
		{
			foreach (var child in module.Children.Values)
				pending.Push(child);
			
			foreach (var (name, members) in module.Members)
			{
				if (otherNames.Contains(name))
					continue;
				
				foreach (var member in members)
				{
					if (member is FunctionSymbol or NamedTypeSymbol or TraitSymbol or GlobalSymbol &&
					    (member is FunctionSymbol || !functionNames.Contains(name)) &&
					    !visible.Contains(member) && IsVisible(member))
						yield return (member, new CompletionImport(name, module.Path,
							modules.FindAssembly(member) == modules.AssemblyName));
				}
			}
		}
	}
	
	public IEnumerable<Symbol> GetModuleMembers(ModulePathSymbol module) => module.Members.Values
		.SelectMany(static members => members)
		.Where(IsVisible)
		.Concat(module.Children.Values);
	
	private TypeSymbol ResolveNamedType(Token name)
	{
		ProbeType(name);
		switch (Resolve(name))
		{
			case NamedTypeSymbol { IsGenericDefinition: true } generic:
				return InstantiateType(generic, [], [], name.SourceLocation);
			
			case TypeParameterSymbol { IsTrait: true } or TypeParameterSymbol { IsValue: true }:
				Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, $"'{name.Text}' is not a type"));
				break;
			
			case TypeSymbol type:
				return type;
			
			case null when NativeSymbols.FindGeneric(name.Text) is { } builtin:
				ReportArgumentCount(name.SourceLocation, builtin);
				break;
			
			case null when name.Text == "never":
				Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation,
					"'never' can only be a return type"));
				
				break;
			
			case null:
				ReportUndefinedType(name);
				break;
			
			case AmbiguousSymbol ambiguous:
				Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, DescribeAmbiguousType(ambiguous)));
				break;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, $"'{name.Text}' is not a type"));
				break;
		}
		
		return NativeSymbols.Invalid;
	}
	
	private TypeSymbol ResolveQualifiedType(QualifiedTypeNode node)
	{
		var name = string.Join('.', node.Parts.Select(static p => p.Text));
		switch (ResolveQualifiedName(node.Parts))
		{
			case NamedTypeSymbol { IsGenericDefinition: true } generic:
				return InstantiateType(generic, [], [], node.SourceLocation);
			
			case TypeSymbol type:
				return type;
			
			case null:
				break;
			
			case AmbiguousSymbol ambiguous:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
					DescribeAmbiguousType(ambiguous, name)));
				
				break;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{name}' is not a type"));
				break;
		}
		
		return NativeSymbols.Invalid;
	}
	
	private void ReportUndefinedType(Token name) => Diagnostics.Add(ReportHidden(name.Text, name.SourceLocation) ??
	                                                                WithImportFixes(
		                                                                DiagnosticReporter.ReportUndefinedType(
			                                                                name.SourceLocation, name.Text,
			                                                                GetVisibleTypeNames()), name.Text,
		                                                                static symbol =>
			                                                                symbol is NamedTypeSymbol or TraitSymbol));
	
	public Diagnostic WithImportFixes(Diagnostic diagnostic, string name, Func<Symbol, bool> accepts)
	{
		var file = File.Syntax;
		var fixes = GetImportableSymbols()
			.Where(entry => entry.Import.Name == name && accepts(entry.Symbol))
			.Select(static entry => entry.Import.Module)
			.Distinct()
			.Order(StringComparer.Ordinal)
			.Select(module => new DiagnosticFix($"Import '{module}.{name}'", ImportEdits.Create(file, module, name)))
			.ToList();
		
		return fixes.Count == 0 ? diagnostic : diagnostic with { Fixes = [..fixes, ..diagnostic.Fixes] };
	}
	
	private IEnumerable<string> GetVisibleTypeNames() => GetAllSymbols()
		.OfType<TypeSymbol>()
		.Select(static t => t.Name)
		.Distinct()
		.Order();
	
	public TypeSymbol? TryResolveExpressionAsType(IExpressionNode expression) => expression switch
	{
		VarExpressionNode or AccessExpressionNode => AsType(FindNamed(expression)),
		IndexerExpressionNode i => TryResolveGenericType(i),
		BorrowExpressionNode { Value: ArrayExpressionNode { Values: [var element] } } b =>
			TryResolveExpressionAsType(element) is { } target ? ResolveBorrowType(target, b.IsMutable) : null,
		TypeExpressionNode t => ResolveType(t.Type),
		_ => null
	};
	
	public Symbol? FindNamed(IExpressionNode expression) => expression switch
	{
		VarExpressionNode v => Resolve(v.Identifier),
		AccessExpressionNode a => ResolveModule(a.Target) is { } module
			? Record(a.Member, ResolveMember(module, a.Member.Text))
			: null,
		_ => null
	};
	
	private static TypeSymbol? AsType(Symbol? symbol) => symbol is TypeSymbol type && !IsNonType(type) ? type : null;
	
	private static bool IsNonType(TypeSymbol type) =>
		type is TypeParameterSymbol { IsTrait: true } or TypeParameterSymbol { IsValue: true };
	
	private TypeSymbol? AsTraitArgument(Symbol? symbol, SourceLocation location) => symbol switch
	{
		TraitSymbol trait => InstantiateTrait(trait, [], [], location) ?? (TypeSymbol)NativeSymbols.Invalid,
		TypeParameterSymbol { IsTrait: true } trait => trait,
		_ => null
	};
	
	public TypeSymbol? ResolveTraitArgument(IExpressionNode expression) => expression switch
	{
		IndexerExpressionNode { Target: VarExpressionNode or AccessExpressionNode } indexer when
			FindIndexedTrait(indexer) is { } found => found is TraitSymbol trait
				? InstantiateTrait(trait,
					ResolveGenericArguments(indexer.Arguments, trait.TypeParameters,
						ResolveGenericArgumentExpression),
					[..indexer.Arguments.Select(static argument => argument.SourceLocation)],
					indexer.SourceLocation) ?? (TypeSymbol)NativeSymbols.Invalid
				: NativeSymbols.Invalid,
		_ => AsTraitArgument(FindNamed(expression), expression.SourceLocation)
	};
	
	private Symbol? FindIndexedTrait(IndexerExpressionNode indexer)
	{
		var symbol = FindNamed(indexer.Target);
		if (symbol is TraitSymbol)
			return symbol;
		
		var all = indexer.Target is AccessExpressionNode access
			? FindQualifiedQuietly(access)
			: ResolveAll(((VarExpressionNode)indexer.Target).Identifier.Text);
		
		if (TypeOverloads.FindFamily(all) is not [TraitSymbol, ..])
			return null;
		
		ImmutableArray<IGenericArgumentNode> arguments =
			[..indexer.Arguments.Select(static argument => new ExpressionArgumentNode(argument))];
		
		if (!TryChooseOverload(ref all, arguments, indexer.Target.SourceLocation.GetText().ToString(),
			    indexer.Target.SourceLocation))
			return NativeSymbols.Invalid;
		
		if (FindNamedToken(indexer.Target) is { } name)
			Record(name, all);
		
		return all;
	}
	
	public TypeSymbol ResolveGenericArgumentExpression(IExpressionNode expression, TypeParameterSymbol? parameter)
	{
		if (expression is AccessExpressionNode { Target: VarExpressionNode target } projected &&
		    Resolve(target.Identifier) is TypeParameterSymbol { IsValue: false, IsTrait: false } owner &&
		    TypePool.GetBounds(owner)
			    .Select(bound => bound.Trait.GetConstant(projected.Member.Text) is { } constant
				    ? new TypeConstantUse(owner, bound, constant)
				    : null)
			    .OfType<TypeConstantUse>().Distinct().ToList() is [var use])
		{
			RecordName?.Invoke(projected.Member, use.Constant);
			return TypePool.GetConstantProjection(use);
		}
		
		var named = expression is VarExpressionNode or AccessExpressionNode ? FindNamed(expression) : null;
		return named switch
		{
			TypeParameterSymbol { IsValue: true } value => value,
			LocalVariableSymbol local when UseParametricArgument(local) is { } argument => argument,
			VariableSymbol => ResolveValueExpression(expression, parameter),
			null when expression is AccessExpressionNode access && ResolveModule(access.Target) is { } module =>
				ReportMissingMember(access, module),
			_ when ResolveTraitArgument(expression) is { } trait => trait,
			_ when expression is VarExpressionNode or TypeExpressionNode or BorrowExpressionNode =>
				ResolveTypeExpression(expression),
			_ when expression is AccessExpressionNode or IndexerExpressionNode &&
			       TryResolveExpressionAsType(expression) is { } type => CheckTypeExpression(expression, type),
			_ => ResolveValueExpression(expression, parameter)
		};
	}
	
	private TypeParameterSymbol? UseParametricArgument(LocalVariableSymbol local)
	{
		if (FindParametricArgument(local) is not { } argument)
			return null;
		
		MarkUsed?.Invoke(local);
		return argument;
	}
	
	private TypeParameterSymbol? FindParametricArgument(LocalVariableSymbol local) =>
		(local.ConstantValue as ParametricConstant)?.Value switch
		{
			ResolvedValueParameterExpressionNode value => value.Parameter,
			ResolvedTypeConstantExpressionNode { Use: { Owner: TypeParameterSymbol, Trait: not null } use } =>
				TypePool.GetConstantProjection(use),
			ResolvedVarExpressionNode { Symbol: LocalVariableSymbol source } => FindParametricArgument(source),
			_ => null
		};
	
	private TypeSymbol ReportMissingMember(AccessExpressionNode access, ModulePathSymbol module)
	{
		Diagnostics.Add(ReportUndefinedMember(access.Member.SourceLocation, module, access.Member.Text));
		return NativeSymbols.Invalid;
	}
	
	private static ImmutableArray<TypeSymbol> ResolveGenericArguments<TNode>(ImmutableArray<TNode> nodes,
		ImmutableArray<TypeParameterSymbol> parameters, Func<TNode, TypeParameterSymbol?, TypeSymbol> resolve) =>
		[..nodes.Select((node, i) => resolve(node, nodes.Length == parameters.Length ? parameters[i] : null))];
	
	private TypeSymbol ResolveValueExpression(IExpressionNode expression, TypeParameterSymbol? parameter)
	{
		if (IsUndeclaredNever(expression))
		{
			Report(expression, "'never' can only be a return type");
			return NativeSymbols.Invalid;
		}
		
		if (parameter is { IsValue: false })
		{
			Report(expression,
				$"'{expression.SourceLocation.GetText()}' is not a {(parameter.IsTrait ? "trait" : "type")}");
			
			return NativeSymbols.Invalid;
		}
		
		var constant = EvaluateConstant?.Invoke(expression, this, null);
		if (constant is not InvalidConstant && parameter?.ValueType is { } valueType and not InvalidType)
			constant = EvaluateConstant!(expression, this, valueType);
		
		if (constant is null)
		{
			var use = FindValueParameterUse?.Invoke(expression, this);
			if (use is { IsArithmetic: true })
				Report(expression, "Cannot use arithmetic on value parameters in generic arguments");
			else if (use is not null || parameter is not null)
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"'{expression.SourceLocation.GetText()}' is not a constant")
				{
					Hints = use is { Hint: var hint } ? [hint] : []
				});
			
			return NativeSymbols.Invalid;
		}
		
		switch (constant)
		{
			case InvalidConstant:
				return NativeSymbols.Invalid;
			
			case IntegerConstant integer:
				return TypePool.GetValueArgument(integer.Value);
			
			default:
				if (parameter is not null)
					Report(expression, $"'{expression.SourceLocation.GetText()}' is not an integer");
				
				return NativeSymbols.Invalid;
		}
	}
	
	private bool IsUndeclaredNever(IExpressionNode expression) =>
		expression is VarExpressionNode { Identifier.Text: "never" } && FindNamed(expression) is null;
	
	private TypeSymbol? TryResolveGenericType(IndexerExpressionNode node)
	{
		if (GenericTypes?.TryGetValue(node, out var cached) == true)
			return cached;
		
		var result = ResolveGenericExpression(node);
		if (GenericTypes is not { } genericTypes)
			return result;
		
		genericTypes[node] = result;
		RecordUndo?.Invoke(() => genericTypes.Remove(node));
		return result;
	}
	
	private TypeSymbol? ResolveGenericExpression(IndexerExpressionNode node)
	{
		var builtin = node.Target is VarExpressionNode name ? NativeSymbols.FindGeneric(name.Identifier.Text) : null;
		var symbol = builtin is not null
			? null
			: node.Target switch
			{
				VarExpressionNode variable => ResolveAll(variable.Identifier),
				AccessExpressionNode access => ResolveModule(access.Target) is { } module
					? ResolveAllMembers(module, access.Member.Text)
					: null,
				_ => null
			};
		
		if (TypeOverloads.FindFamily(symbol) is [NamedTypeSymbol, ..])
		{
			ImmutableArray<IGenericArgumentNode> arguments =
				[..node.Arguments.Select(static argument => new ExpressionArgumentNode(argument))];
			
			if (!TryChooseOverload(ref symbol, arguments, node.Target.SourceLocation.GetText().ToString(),
				    node.Target.SourceLocation))
				return NativeSymbols.Invalid;
		}
		
		if (FindNamedToken(node.Target) is { } nameToken)
			Record(nameToken, symbol);
		
		return builtin is not null || symbol is TypeSymbol type && !IsNonType(type)
			? ResolveGenericApplication(symbol, builtin,
				[..node.Arguments.Select(static argument => new ExpressionArgumentNode(argument))], node.SourceLocation)
			: null;
	}
	
	private TypeSymbol ResolveGenericApplication(Symbol? symbol, BuiltinGenericSymbol? builtin,
		ImmutableArray<IGenericArgumentNode> arguments, SourceLocation location)
	{
		if (builtin is not null)
			return ResolveBuiltinGeneric(builtin, arguments, location);
		
		if (symbol is NamedTypeSymbol { IsGenericDefinition: true } definition)
			return InstantiateType(definition,
				ResolveGenericArguments(arguments, definition.TypeParameters, ResolveGenericTypeArgument),
				[..arguments.Select(static argument => argument.SourceLocation)], location);
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"'{symbol?.Name}' takes no generic arguments"));
		return NativeSymbols.Invalid;
	}
	
	private TypeSymbol ResolveBuiltinGeneric(BuiltinGenericSymbol builtin,
		ImmutableArray<IGenericArgumentNode> arguments,
		SourceLocation location)
	{
		switch (builtin.Kind)
		{
			case BuiltinGenericKind.FStr:
				return arguments is [var trait]
					? ResolveFStrArgument(trait)
					: ReportArgumentCount(location, builtin);
			
			case BuiltinGenericKind.Array:
				return InstantiateArray(
					ResolveGenericArguments(arguments, builtin.TypeParameters, ResolveGenericTypeArgument),
					[..arguments.Select(static argument => argument.SourceLocation)], location);
			
			default:
				if (arguments is not [var argument])
					return ReportArgumentCount(location, builtin);
				
				var baseType = ResolveTypeArgument(argument) ?? ReportNotType(argument);
				return baseType is InvalidType ? baseType : TypePool.GetPointerType(baseType);
		}
	}
	
	public static Diagnostic ReportGenericArgumentCount(SourceLocation location, NamedTypeSymbol generic) =>
		ReportGenericArgumentCount(location, generic.Name, generic.TypeParameters);
	
	public static Diagnostic ReportGenericArgumentCount(SourceLocation location, string name,
		ImmutableArray<TypeParameterSymbol> parameters) => new(DiagnosticSeverity.Error, location,
		$"'{name}' takes {DescribeGenericArguments(TypePool.CountRequiredArguments(parameters), parameters.Length)}");
	
	private static string DescribeGenericArguments(int minimum, int maximum) => (minimum, maximum) switch
	{
		_ when minimum == maximum => DescribeGenericArguments(maximum),
		(0, 1) => "at most one generic argument",
		(0, _) => $"at most {maximum} generic arguments",
		_ when maximum - minimum == 1 => $"{minimum} or {maximum} generic arguments",
		_ => $"{minimum} to {maximum} generic arguments"
	};
	
	public static Diagnostic ReportGenericArgumentCount(SourceLocation location, string name, int count) =>
		new(DiagnosticSeverity.Error, location, $"'{name}' takes {DescribeGenericArguments(count)}");
	
	public static string DescribeGenericArguments(int count) => count switch
	{
		0 => "no generic arguments",
		1 => "one generic argument",
		_ => $"{count} generic arguments"
	};
	
	private ImmutableArray<TypeSymbol>? CheckGenericArguments(string name,
		ImmutableArray<TypeParameterSymbol> parameters, IReadOnlyList<TypeSymbol> arguments,
		IReadOnlyList<SourceLocation> locations, SourceLocation location,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? outer = null)
	{
		if (TypePool.CompleteArguments(parameters, arguments, outer) is not { } completed)
		{
			Diagnostics.Add(ReportGenericArgumentCount(location, name, parameters));
			return null;
		}
		
		var map = TypePool.CreateMap(parameters, completed);
		var isValid = true;
		for (var i = 0; i < completed.Length; i++)
		{
			var parameter = parameters[i];
			var argumentLocation = i < locations.Count ? locations[i] : location;
			if (DeferConstraintCheck?.Invoke(new(parameter, completed[i], map, argumentLocation)) == true ||
			    TypePool.FindConstraintViolation(parameter, completed[i], map) is not { } violation)
				continue;
			
			Diagnostics.Add(violation.At(argumentLocation));
			isValid = false;
		}
		
		return isValid ? completed : null;
	}
	
	private TypeSymbol InstantiateType(NamedTypeSymbol definition, IReadOnlyList<TypeSymbol> arguments,
		IReadOnlyList<SourceLocation> locations, SourceLocation location)
	{
		if (CheckGenericArguments(definition.Name, definition.TypeParameters, arguments, locations, location) is not
		    { } completed)
			return NativeSymbols.Invalid;
		
		var type = TypePool.Instantiate(definition, completed);
		TrackInstance?.Invoke(this, type, location);
		AddSizeCheck(type, location);
		return type;
	}
	
	private TypeSymbol InstantiateArray(IReadOnlyList<TypeSymbol> arguments, IReadOnlyList<SourceLocation> locations,
		SourceLocation location)
	{
		if (CheckGenericArguments(NativeSymbols.GenericArray.Name, NativeSymbols.GenericArray.TypeParameters,
			    arguments,
			    locations, location) is null ||
		    arguments is not [not InvalidType and var element, not InvalidType and var length])
			return NativeSymbols.Invalid;
		
		var type = TypePool.GetArrayType(element, length);
		AddSizeCheck(type, location);
		return type;
	}
	
	private void AddSizeCheck(TypeSymbol type, SourceLocation location)
	{
		var checks = TypePool.SizeChecks;
		checks.Add((type, location, ContainingFunction?.Symbol, Containment?.Current ?? default));
		RecordUndo?.Invoke(() => checks.RemoveAt(checks.Count - 1));
	}
	
	public TypeSymbol ResolveTypeExpression(IExpressionNode expression) =>
		CheckTypeExpression(expression, TryResolveExpressionAsType(expression));
	
	private TypeSymbol CheckTypeExpression(IExpressionNode expression, TypeSymbol? type)
	{
		switch (type)
		{
			case NamedTypeSymbol { IsGenericDefinition: true } generic when expression is not IndexerExpressionNode:
				return InstantiateType(generic, [], [], expression.SourceLocation);
			
			case not null:
				return type;
			
			case null when FindNamed(expression) is AmbiguousSymbol ambiguous &&
			               TypeOverloads.FindFamily(ambiguous) is not null:
				Report(expression,
					DescribeAmbiguousType(ambiguous, expression.SourceLocation.GetText().ToString()));
				
				return NativeSymbols.Invalid;
			
			default:
				Report(expression, IsUndeclaredNever(expression)
					? "'never' can only be a return type"
					: $"'{expression.SourceLocation.GetText()}' is not a type");
				
				return NativeSymbols.Invalid;
		}
	}
	
	public TypeSymbol ResolveDefaultArgument(TypeParameterNode node, TypeParameterSymbol parameter) => node switch
	{
		{ DefaultType: { } type } => ResolveGenericTypeArgument(new TypeArgumentNode(type), parameter),
		{ DefaultValue: { } value } => ResolveGenericTypeArgument(new ExpressionArgumentNode(value), parameter),
		_ => NativeSymbols.Invalid
	};
	
	private TypeSymbol ResolveGenericTypeArgument(IGenericArgumentNode node, TypeParameterSymbol? parameter) =>
		node switch
		{
			TypeArgumentNode { Type: IdentifierTypeNode type } when
				AsTraitArgument(Resolve(type.Token), type.SourceLocation) is { } trait => trait,
			TypeArgumentNode { Type: QualifiedTypeNode type } when
				AsTraitArgument(FindQualified(type.Parts), type.SourceLocation) is { } trait => trait,
			TypeArgumentNode { Type: GenericTypeNode type } when
				FindQualifiedQuietly([..type.Qualifiers, type.Identifier]) is var named &&
				(named is TraitSymbol || TypeOverloads.FindFamily(named) is [TraitSymbol, ..]) =>
				ResolveTrait(type) ?? (TypeSymbol)NativeSymbols.Invalid,
			TypeArgumentNode argument => ResolveType(argument.Type),
			IdentifierArgumentNode argument => Resolve(argument.Identifier) switch
			{
				TypeParameterSymbol { IsValue: true } value => value,
				LocalVariableSymbol local when UseParametricArgument(local) is { } parametric => parametric,
				VariableSymbol => ResolveValueExpression(new VarExpressionNode(argument.Identifier), parameter),
				var symbol => AsTraitArgument(symbol, argument.SourceLocation) ?? ResolveNamedType(argument.Identifier)
			},
			ExpressionArgumentNode argument => ResolveGenericArgumentExpression(argument.Expression, parameter),
			_ => NativeSymbols.Invalid
		};
	
	private Symbol? FindQualified(ImmutableArray<Token> parts)
	{
		var symbol = Resolve(parts[0].Text);
		foreach (var part in parts.Skip(1))
			symbol = symbol is ModulePathSymbol module ? Record(part, ResolveMember(module, part.Text)) : null;
		
		return symbol;
	}
	
	private TypeSymbol? ResolveTypeArgument(IGenericArgumentNode node) => node switch
	{
		TypeArgumentNode t => ResolveType(t.Type),
		IdentifierArgumentNode i => Resolve(i.Identifier) switch
		{
			null or TypeParameterSymbol { IsTrait: true } or TypeParameterSymbol { IsValue: true }
				or NamedTypeSymbol { IsGenericDefinition: true } => ResolveNamedType(i.Identifier),
			var symbol => symbol as TypeSymbol
		},
		ExpressionArgumentNode e => TryResolveExpressionAsType(e.Expression) is { } type
			? CheckTypeExpression(e.Expression, type)
			: null,
		_ => null
	};
	
	private void Report(IExpressionNode node, string message) =>
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, message));
	
	private TypeSymbol ResolveGenericType(GenericTypeNode node)
	{
		var name = node.Identifier;
		var builtin = node.Qualifiers.IsEmpty ? NativeSymbols.FindGeneric(name.Text) : null;
		if (builtin is not null)
			return ResolveGenericApplication(null, builtin, node.Arguments, node.SourceLocation);
		
		ImmutableArray<Token> parts = [..node.Qualifiers, name];
		var symbol = node.Qualifiers.IsEmpty ? ResolveAll(name) : ResolveQualifiedName(parts, true);
		if (!TryChooseOverload(ref symbol, node.Arguments, string.Join('.', parts.Select(static part => part.Text)),
			    GetSpan(parts)))
			return NativeSymbols.Invalid;
		
		switch (symbol)
		{
			case NamedTypeSymbol { IsGenericDefinition: true } generic when
				ReportRedundantBrackets(node, generic.TypeParameters):
				return InstantiateType(generic, [], [], node.SourceLocation);
			
			case TypeSymbol:
				return ResolveGenericApplication(symbol, null, node.Arguments, node.SourceLocation);
			
			case null when node.Qualifiers.IsEmpty:
				Diagnostics.Add(ReportHidden(name.Text, name.SourceLocation) ??
				                DiagnosticReporter.ReportUndefinedType(name.SourceLocation, name.Text,
					                GetVisibleTypeNames().Concat(NativeSymbols.GenericNames)));
				
				break;
			
			case null:
				break;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, $"'{name.Text}' is not a type"));
				break;
		}
		
		return NativeSymbols.Invalid;
	}
	
	private bool ReportRedundantBrackets(GenericTypeNode node, ImmutableArray<TypeParameterSymbol> parameters)
	{
		if (node.EmptyBrackets is not { } brackets || TypePool.CountRequiredArguments(parameters) > 0)
			return false;
		
		Diagnostics.Add(DiagnosticFix.Redundant(brackets));
		return true;
	}
	
	private TypeSymbol ReportNotType(IGenericArgumentNode node)
	{
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, node is ExpressionArgumentNode
		{
			Expression: var expression
		} && IsUndeclaredNever(expression)
			? "'never' can only be a return type"
			: $"'{node.SourceLocation.GetText()}' is not a type"));
		
		return NativeSymbols.Invalid;
	}
	
	private TypeSymbol ResolveFStrArgument(IGenericArgumentNode node) => node switch
	{
		TypeArgumentNode argument => CreateFStrType(argument, ResolveTraitReference(argument.Type)),
		IdentifierArgumentNode argument =>
			CreateFStrType(argument, ResolveTraitReference(new IdentifierTypeNode(argument.Identifier))),
		ExpressionArgumentNode argument => CreateFStrType(argument.Expression),
		_ => NativeSymbols.Invalid
	};
	
	private TypeSymbol CreateFStrType(IExpressionNode trait)
	{
		var argument = ResolveTraitArgument(trait);
		if (GetTraitDyn(argument) is { } dyn)
			return TypePool.GetFStrType(dyn);
		
		if (argument is not InvalidType)
			Report(trait, $"'{trait.SourceLocation.GetText()}' is not a trait");
		
		return NativeSymbols.Invalid;
	}
	
	private TypeSymbol CreateFStrType(IGenericArgumentNode node, TypeSymbol? trait)
	{
		if (GetTraitDyn(trait) is { } dyn)
			return TypePool.GetFStrType(dyn);
		
		if (trait is not (null or InvalidType))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"'{node.SourceLocation.GetText()}' is not a trait"));
		
		return NativeSymbols.Invalid;
	}
	
	private DynType? GetTraitDyn(TypeSymbol? trait) => trait switch
	{
		TraitType instance => TypePool.GetDynType(instance),
		TypeParameterSymbol { IsTrait: true } parameter => TypePool.GetDynType(parameter),
		_ => null
	};
	
	private TypeSymbol ReportArgumentCount(SourceLocation location, BuiltinGenericSymbol builtin)
	{
		Diagnostics.Add(ReportGenericArgumentCount(location, builtin.Name, builtin.TypeParameters.Length));
		return NativeSymbols.Invalid;
	}
}

public readonly record struct ConstraintCheck
(
	TypeParameterSymbol Parameter,
	TypeSymbol Argument,
	IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> Map,
	SourceLocation Location
);