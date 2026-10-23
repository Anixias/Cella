using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void ReportDuplicateDeclarations()
	{
		foreach (var module in _symbolTable.ModuleSymbols.Values)
		{
			var declarationsByName = module.Files
				.SelectMany(static file => file.Syntax.Declarations.Select(declaration => (file, declaration)))
				.Where(d => _symbolTable.DeclarationSymbols.ContainsKey(d.declaration) && d.declaration is not ImplNode)
				.Select(d => (d.file, d.declaration, symbol: _symbolTable.DeclarationSymbols[d.declaration]))
				.ToLookup(static d => d.symbol.Name);
			
			foreach (var sameName in declarationsByName)
			{
				if (ReportPlainOverloadConflicts(sameName.ToList()))
					continue;
				
				foreach (var (file, declaration, symbol) in sameName)
				{
					var visibleTogether = sameName
						.Where(other => AreVisibleTogether(symbol, file, other.symbol, other.file))
						.Select(static other => other.declaration);
					
					if (FindConflict(declaration, visibleTogether) is not { } diagnostic)
						continue;
					
					using var frame = Own(declaration);
					Diagnostics.Add(diagnostic);
					_conflictingDeclarations.Add(symbol);
				}
			}
		}
	}
	
	private bool ReportPlainOverloadConflicts(
		List<(FileSymbol File, IDeclarationNode Declaration, Symbol Symbol)> sameName)
	{
		if (sameName.Count < 2 ||
		    sameName.Any(static d => d.Symbol is not FunctionSymbol
		    {
			    Kind: not FunctionKind.External, DeclaredTypeParameters.IsEmpty: true
		    }) ||
		    sameName.Select(static d => d.File).Distinct().Count() > 1 &&
		    sameName.Any(static d => ModuleIndex.GetVisibility(d.Symbol) == Visibility.Private))
			return false;
		
		var signatures = sameName
			.Select(d => (d.Declaration, Function: (FunctionSymbol)d.Symbol,
				_builder.Functions[(FunctionSymbol)d.Symbol].Signature))
			.ToList();
		
		foreach (var (declaration, function, signature) in signatures)
		{
			var message = DescribeConflict(function, signature,
				signatures.Where(other => other.Function != function).Select(static other => other.Signature));
			
			if (message is null)
				continue;
			
			using var frame = Own(declaration);
			Diagnostics.Add(new(DiagnosticSeverity.Error, GetIdentifier(declaration).SourceLocation, message));
			_conflictingDeclarations.Add(function);
		}
		
		return true;
	}
	
	private void ReportPlainNameConflicts()
	{
		var definitions = _symbolTable.DeclarationSymbols.Values
			.Distinct()
			.SelectMany(FindExternalDefinitions)
			.GroupBy(static definition => definition.Name)
			.Where(static sameName => sameName.Count() > 1);
		
		foreach (var definition in definitions.SelectMany(static sameName => sameName))
		{
			using var frame = Own(definition.Symbol);
			Diagnostics.Add(new(DiagnosticSeverity.Error, definition.Identifier.SourceLocation,
				$"'{definition.Name}' is defined by more than one 'ext' declaration"));
		}
	}
	
	private static IEnumerable<ExternalDefinition> FindExternalDefinitions(Symbol symbol) => symbol switch
	{
		FunctionSymbol { Syntax: FunctionNode node, ExternalName: { } name } => [new(name, node.Identifier, symbol)],
		GlobalSymbol { IsImport: false, ExternalName: { } name } global =>
			[new(name, global.Syntax.Identifier, symbol)],
		_ => []
	};
	
	private readonly record struct ExternalDefinition(string Name, Token Identifier, Symbol Symbol);
	
	private static bool AreVisibleTogether(Symbol first, FileSymbol firstFile, Symbol second, FileSymbol secondFile) =>
		firstFile.Module == secondFile.Module && (firstFile == secondFile ||
		                                          ModuleIndex.GetVisibility(first) != Visibility.Private ||
		                                          ModuleIndex.GetVisibility(second) != Visibility.Private);
	
	private Diagnostic? FindConflict(IDeclarationNode declaration, IEnumerable<IDeclarationNode> sameName)
	{
		var symbol = _symbolTable.DeclarationSymbols[declaration];
		var others = sameName
			.Where(other => other != declaration)
			.Select(other => _symbolTable.DeclarationSymbols[other])
			.ToList();
		
		if (others.Count == 0)
			return null;
		
		var location = GetIdentifier(declaration).SourceLocation;
		if (others.All(other => TypeOverloads.CanOverload(symbol, other)))
			return DescribeTypeConflict(symbol, others) is { } typeConflict
				? new(DiagnosticSeverity.Error, location, typeConflict)
				: null;
		
		if (symbol is not FunctionSymbol function || others.Any(static s => s is not FunctionSymbol))
			return new(DiagnosticSeverity.Error, location,
				$"'{symbol.Name}' is declared more than once in this module");
		
		var isExternal = function.Kind == FunctionKind.External;
		if (isExternal && others.Any(static s => s is FunctionSymbol { Kind: FunctionKind.External }))
			return new(DiagnosticSeverity.Error, location, "'ext' functions cannot be overloaded");
		
		var overloads = others.Cast<FunctionSymbol>().Where(other => HasReceiver(other) == HasReceiver(function))
			.ToList();
		
		var message = DescribeConflict(function, _builder.Functions[function].Signature,
			overloads
				.Select(other => AlignTypeParameters(function, other, _builder.Functions[other].Signature))
				.OfType<FunctionSignature>()) ?? DescribeDefaultedConflict(function, overloads);
		
		return message is null ? null : new(DiagnosticSeverity.Error, location, message);
	}
	
	private static string? DescribeTypeConflict(Symbol symbol, List<Symbol> others) =>
		others.Any(other => TypeOverloads.HaveSameParameters(symbol, other))
			? $"'{symbol.Name}' is declared more than once in this module"
			: others.Any(other => TypeOverloads.Overlap(symbol, other))
				? $"'{symbol.Name}' declarations cannot take the same generic arguments"
				: null;
	
	private string? DescribeDefaultedConflict(FunctionSymbol function, IEnumerable<FunctionSymbol> overloads) =>
		overloads
			.Where(other => other.DeclaredTypeParameters.Length != function.DeclaredTypeParameters.Length)
			.Select(other => AlignDefaultedTypeParameters(function, other))
			.Any(pair => pair is var (mine, theirs) && DescribeConflict(function, mine, [theirs]) is not null)
			? $"'{function.Name}' overloads cannot accept the same arguments"
			: null;
	
	private (FunctionSignature Mine, FunctionSignature Theirs)? AlignDefaultedTypeParameters(FunctionSymbol function,
		FunctionSymbol other)
	{
		var (shorter, longer) = function.DeclaredTypeParameters.Length < other.DeclaredTypeParameters.Length
			? (function, other)
			: (other, function);
		
		var kept = shorter.DeclaredTypeParameters;
		var extended = longer.DeclaredTypeParameters;
		if (TypePool.CountRequiredArguments(extended) > kept.Length)
			return null;
		
		var map = TypePool.CreateMap(extended, kept);
		if (!kept.Zip(extended).All(pair => HaveSameConstraints(pair.First, pair.Second, map)))
			return null;
		
		for (var i = kept.Length; i < extended.Length; i++)
			map[extended[i]] = _typePool.Substitute(_typePool.GetDefaultArgument(extended[i])!, map);
		
		var aligned = _typePool.SubstituteSignature(_builder.Functions[longer].Signature, map);
		var plain = _builder.Functions[shorter].Signature;
		return longer == function ? (aligned, plain) : (plain, aligned);
	}
	
	private static bool HasReceiver(FunctionSymbol function) => function.Syntax is FunctionNode { Receiver: not null };
	
	private FunctionSignature? AlignTypeParameters(FunctionSymbol function, FunctionSymbol other,
		FunctionSignature signature)
	{
		var mine = function.DeclaredTypeParameters;
		var theirs = other.DeclaredTypeParameters;
		if (mine.Length != theirs.Length)
			return null;
		
		if (mine.IsEmpty)
			return signature;
		
		var map = TypePool.CreateMap(theirs, mine);
		return mine.Zip(theirs).All(pair => HaveSameConstraints(pair.First, pair.Second, map))
			? _typePool.SubstituteSignature(signature, map)
			: signature;
	}
	
	private bool HaveSameConstraints(TypeParameterSymbol first, TypeParameterSymbol second,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map) =>
		first.IsNoref == second.IsNoref && first.HasNull == second.HasNull && first.IsCopy == second.IsCopy &&
		first.IsAtomic == second.IsAtomic && first.HasDrop == second.HasDrop && first.HasNew == second.HasNew &&
		first.IsExt == second.IsExt &&
		first.IsTrait == second.IsTrait && first.IsValue == second.IsValue &&
		first.ValueType == (second.ValueType is { } valueType ? _typePool.Substitute(valueType, map) : null) &&
		HaveSameBounds(_typePool.GetDeclaredBounds(first),
			_typePool.GetDeclaredBounds(second).Select(bound => _typePool.SubstituteTrait(bound, map))) &&
		_typePool.GetParameterBounds(first).ToHashSet<TypeSymbol>()
			.SetEquals(_typePool.GetParameterBounds(second).Select(bound => _typePool.Substitute(bound, map))) &&
		_typePool.GetFunctionBounds(first).ToHashSet<TypeSymbol>()
			.SetEquals(_typePool.GetFunctionBounds(second).Select(bound => _typePool.Substitute(bound, map))) &&
		HaveSameConstructors(_typePool.GetConstructors(first), _typePool.GetConstructors(second), map);
	
	private static bool HaveSameBounds(IEnumerable<TraitType> first, IEnumerable<TraitType> second)
	{
		var remaining = second.ToList();
		foreach (var bound in first)
		{
			var index = remaining.FindIndex(other =>
				other.Trait == bound.Trait && other.Arguments.SequenceEqual(bound.Arguments));
			
			if (index < 0)
				return false;
			
			remaining.RemoveAt(index);
		}
		
		return remaining.Count == 0;
	}
	
	private bool HaveSameConstructors(IReadOnlyList<FunctionInfo> first, IReadOnlyList<FunctionInfo> second,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map) =>
		first.Count == second.Count && first.Zip(second).All(pair =>
		{
			var other = _typePool.SubstituteSignature(pair.Second.Signature, map);
			return pair.First.Signature.ParameterModes.SequenceEqual(other.ParameterModes) &&
			       pair.First.Signature.ParameterTypes.Skip(1).SequenceEqual(other.ParameterTypes.Skip(1));
		});
	
	private string? DescribeConflict(FunctionSymbol function, FunctionSignature signature,
		IEnumerable<FunctionSignature> others) =>
		DescribeConflict(function.Name, signature, others, CountFixedModes(function, signature));
	
	private string? DescribeConflict(string name, FunctionSignature signature, IEnumerable<FunctionSignature> others,
		int fixedModes)
	{
		var overloads = others.ToList();
		var sameParameters = overloads.Where(other => HasSameParameters(other, signature)).ToList();
		if (sameParameters.Count == 0)
			return overloads.Any(other => AcceptsSameArguments(signature, other))
				? $"'{name}' overloads cannot accept the same arguments"
				: null;
		
		var overlaps = sameParameters
			.Select(other => (Other: other, Overlap: CompareModes(signature, other, fixedModes)))
			.ToList();
		
		var sameModes = overlaps.Where(static pair => pair.Overlap == ModeOverlap.Same).ToList();
		if (sameModes.Count > 0)
			return sameModes.Any(pair => pair.Other.ReturnType == signature.ReturnType)
				? $"'{name}' is declared more than once with the same signature"
				: $"'{name}' overloads cannot differ only in return type";
		
		var fixedOverlaps = overlaps.Where(static pair => pair.Overlap == ModeOverlap.Fixed).ToList();
		if (fixedOverlaps.Count > 0)
			return fixedOverlaps.Any(pair => pair.Other.ReturnType == signature.ReturnType)
				? $"'{name}' overloads cannot differ only in parameter modes"
				: $"'{name}' overloads cannot differ only in parameter modes and return type";
		
		return overlaps.Any(static pair => pair.Overlap == ModeOverlap.Tie)
			? $"'{name}' overloads cannot accept the same arguments"
			: null;
	}
	
	private enum ModeOverlap
	{
		Same,
		Fixed,
		Tie,
		Distinct
	}
	
	private static ModeOverlap CompareModes(FunctionSignature first, FunctionSignature second, int fixedModes)
	{
		var differs = false;
		var firstTakesValue = false;
		var secondTakesValue = false;
		var separates = false;
		for (var i = 0; i < first.ParameterTypes.Length; i++)
		{
			var (a, b) = (first.GetMode(i), second.GetMode(i));
			if (a == b)
				continue;
			
			if (i < fixedModes)
				return ModeOverlap.Fixed;
			
			differs = true;
			separates |= a == ParameterMode.Mut || b == ParameterMode.Mut;
			firstTakesValue |= a == ParameterMode.Value;
			secondTakesValue |= b == ParameterMode.Value;
		}
		
		return !differs ? ModeOverlap.Same
			: !separates && firstTakesValue && secondTakesValue ? ModeOverlap.Tie
			: ModeOverlap.Distinct;
	}
	
	private static int CountFixedModes(FunctionSymbol function, FunctionSignature signature) => function.Syntax switch
	{
		FunctionNode { Identifier.Type: not TokenType.Identifier } => signature.ParameterTypes.Length,
		FunctionNode { Receiver: not null } => 1,
		_ => 0
	};
	
	private void ReportModuleConflicts()
	{
		foreach (var module in _symbolTable.ModuleSymbols.Values)
		{
			var path = Modules.Find(module.Name)!;
			var declarations = module.Files
				.SelectMany(static f => f.Syntax.Declarations)
				.Where(d => _symbolTable.DeclarationSymbols.ContainsKey(d) && d is not ImplNode);
			
			foreach (var declaration in declarations)
			{
				var name = _symbolTable.DeclarationSymbols[declaration].Name;
				if (path.Children.TryGetValue(name, out var child) && Modules.IsLocal(child))
					Diagnostics.Add(ReportModuleConflict(GetIdentifier(declaration).SourceLocation, path, name));
			}
			
			if (path.Parent is not { } parent || !parent.Members.ContainsKey(path.Name))
				continue;
			
			foreach (var file in module.Files)
				Diagnostics.Add(ReportModuleConflict(file.Syntax.ModuleName.SourceLocation, parent, path.Name));
		}
	}
	
	private static Diagnostic ReportModuleConflict(SourceLocation location, ModulePathSymbol parent, string name) =>
		new(DiagnosticSeverity.Error, location,
			$"'{parent.Path}.{name}' is both a module and a member of '{parent.Path}'");
	
	private bool AcceptsSameArguments(FunctionSignature first, FunctionSignature second) =>
		!first.IsVariadic && !second.IsVariadic &&
		Enumerable.Range(0, Math.Max(first.ParameterTypes.Length, second.ParameterTypes.Length) + 1).Any(count =>
			FindArgumentTypes(first, count) is { } firstTypes && FindArgumentTypes(second, count) is { } secondTypes &&
			firstTypes.SequenceEqual(secondTypes));
	
	private TypeSymbol[]? FindArgumentTypes(FunctionSignature signature, int count)
	{
		var fixedCount = signature.FixedCount;
		if (count < fixedCount - signature.Defaults.Length || count > fixedCount && !signature.HasVariadicParameter)
			return null;
		
		var element = signature.HasVariadicParameter
			? _typePool.FindVariadicElement(signature.ParameterTypes[^1])
			: null;
		
		if (signature.HasVariadicParameter && element is null)
			return null;
		
		TypeSymbol[] types =
			[..Enumerable.Range(0, count).Select(i => i < fixedCount ? signature.GetDeclaredType(i) : element!)];
		
		return types.Any(static type => type is InvalidType) ? null : types;
	}
	
	private static bool HasSameParameters(FunctionSignature first, FunctionSignature second) =>
		first.IsVariadic == second.IsVariadic && first.HasVariadicParameter == second.HasVariadicParameter &&
		first.ParameterTypes.Length == second.ParameterTypes.Length &&
		Enumerable.Range(0, first.ParameterTypes.Length).All(i =>
			first.GetDeclaredType(i) is not InvalidType && first.GetDeclaredType(i) == second.GetDeclaredType(i));
	
	private static Token GetIdentifier(IDeclarationNode declaration) => declaration switch
	{
		FunctionNode node => node.Identifier,
		ExternalFunctionNode node => node.Identifier,
		ConstructorNode node => node.Keyword,
		RecordNode node => node.Identifier,
		EnumNode node => node.Identifier,
		GlobalNode node => node.Identifier,
		TraitNode node => node.Identifier,
		_ => throw new InvalidOperationException()
	};
}