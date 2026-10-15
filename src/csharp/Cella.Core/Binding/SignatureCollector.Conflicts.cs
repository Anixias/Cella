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
					
					if (FindConflict(declaration, visibleTogether) is { } diagnostic)
						Diagnostics.Add(diagnostic);
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
		
		var groups = signatures
			.Where(static entry => Enumerable.Range(0, entry.Signature.ParameterTypes.Length)
				.All(i => entry.Signature.GetDeclaredType(i) is not InvalidType))
			.GroupBy(static entry => new ParameterKey(entry.Signature), static entry => entry.Signature)
			.ToDictionary(static group => group.Key, static group => new OverloadGroup(group.ToList()));
		
		foreach (var (declaration, function, signature) in signatures)
		{
			var message = groups.TryGetValue(new ParameterKey(signature), out var group) && group.Count > 1
				? group.DescribeConflict(function.Name, signature)
				: DescribeConflict(function.Name, signature,
					signatures.Where(other => other.Function != function).Select(static other => other.Signature));
			
			if (message is not null)
				Diagnostics.Add(new(DiagnosticSeverity.Error, GetIdentifier(declaration).SourceLocation, message));
		}
		
		return true;
	}
	
	private sealed class OverloadGroup(List<FunctionSignature> signatures)
	{
		private readonly Dictionary<string, int> _modes = signatures.CountBy(DescribeModes).ToDictionary();
		
		private readonly Dictionary<TypeSymbol, int> _returns =
			signatures.CountBy(static signature => signature.ReturnType).ToDictionary();
		
		private readonly Dictionary<(string, TypeSymbol), int> _exact = signatures
			.CountBy(static signature => (DescribeModes(signature), signature.ReturnType))
			.ToDictionary();
		
		public int Count => signatures.Count;
		
		public string DescribeConflict(string name, FunctionSignature signature)
		{
			var modes = DescribeModes(signature);
			return (_modes[modes] > 1, _returns[signature.ReturnType] > 1) switch
			{
				(true, _) when _exact[(modes, signature.ReturnType)] > 1 =>
					$"'{name}' is declared more than once with the same signature",
				(true, _) => $"'{name}' overloads cannot differ only in return type",
				(false, true) => $"'{name}' overloads cannot differ only in parameter modes",
				(false, false) => $"'{name}' overloads cannot differ only in parameter modes and return type"
			};
		}
		
		private static string DescribeModes(FunctionSignature signature) =>
			string.Join(",", signature.ParameterModes);
	}
	
	private readonly struct ParameterKey(FunctionSignature signature) : IEquatable<ParameterKey>
	{
		private readonly FunctionSignature _signature = signature;
		
		public bool Equals(ParameterKey other) => HasSameParameters(_signature, other._signature);
		public override bool Equals(object? obj) => obj is ParameterKey other && Equals(other);
		
		public override int GetHashCode()
		{
			var hash = new HashCode();
			hash.Add(_signature.IsVariadic);
			for (var i = 0; i < _signature.ParameterTypes.Length; i++)
				hash.Add(_signature.GetDeclaredType(i));
			
			return hash.ToHashCode();
		}
	}
	
	private void ReportPlainNameConflicts()
	{
		var definitions = _symbolTable.DeclarationSymbols.Values
			.SelectMany(FindExternalDefinitions)
			.GroupBy(static definition => definition.Name)
			.Where(static sameName => sameName
				.Select(static definition => (definition.Module, definition.Identifier.Text))
				.Distinct()
				.Count() > 1);
		
		foreach (var definition in definitions.SelectMany(static sameName => sameName))
			Diagnostics.Add(new(DiagnosticSeverity.Error, definition.Identifier.SourceLocation,
				$"'{definition.Name}' is defined by more than one 'ext' declaration"));
	}
	
	private IEnumerable<ExternalDefinition> FindExternalDefinitions(Symbol symbol) => symbol switch
	{
		FunctionSymbol { Syntax: FunctionNode node, ExternalName: { } name } function =>
			[new(name, node.Identifier, _builder.Functions[function].File?.Module)],
		GlobalSymbol { IsImport: false, ExternalName: { } name } global =>
			[new(name, global.Syntax.Identifier, GetGlobalInfo(global)?.File.Module)],
		_ => []
	};
	
	private readonly record struct ExternalDefinition(string Name, Token Identifier, ModuleSymbol? Module);
	
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
		if (symbol is not FunctionSymbol function || others.Any(static s => s is not FunctionSymbol))
			return new(DiagnosticSeverity.Error, location,
				$"'{symbol.Name}' is declared more than once in this module");
		
		var isExternal = function.Kind == FunctionKind.External;
		if (isExternal && others.Any(static s => s is FunctionSymbol { Kind: FunctionKind.External }))
			return new(DiagnosticSeverity.Error, location, "'ext' functions cannot be overloaded");
		
		var message = DescribeConflict(symbol.Name, _builder.Functions[function].Signature,
			others.Cast<FunctionSymbol>()
				.Where(other => HasReceiver(other) == HasReceiver(function))
				.Select(other => AlignTypeParameters(function, other, _builder.Functions[other].Signature))
				.OfType<FunctionSignature>());
		
		return message is null ? null : new(DiagnosticSeverity.Error, location, message);
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
	
	private string? DescribeConflict(string name, FunctionSignature signature, IEnumerable<FunctionSignature> others)
	{
		var overloads = others.ToList();
		var sameParameters = overloads.Where(other => HasSameParameters(other, signature)).ToList();
		if (sameParameters.Count == 0)
			return overloads.Any(other => AcceptsSameArguments(signature, other))
				? $"'{name}' overloads cannot accept the same arguments"
				: null;
		
		var sameModes = sameParameters
			.Where(other => other.ParameterModes.SequenceEqual(signature.ParameterModes))
			.ToList();
		
		var sameReturn = sameParameters.Any(other => other.ReturnType == signature.ReturnType);
		return (sameModes.Count > 0, sameReturn) switch
		{
			(true, _) when sameModes.Any(other => other.ReturnType == signature.ReturnType) =>
				$"'{name}' is declared more than once with the same signature",
			(true, _) => $"'{name}' overloads cannot differ only in return type",
			(false, true) => $"'{name}' overloads cannot differ only in parameter modes",
			(false, false) => $"'{name}' overloads cannot differ only in parameter modes and return type"
		};
	}
	
	private void ReportExternalConflicts()
	{
		var imports = _builder.Functions.Keys
			.Where(static function => function.Syntax is ExternalFunctionNode)
			.GroupBy(static function => function.ExternalName);
		
		foreach (var group in imports)
		{
			var functions = group.ToList();
			var byModule = functions.GroupBy(function => _declarations[function].Context.File.Module).ToList();
			var first = _builder.Functions[functions[0]].Signature;
			if (byModule.Count < 2 ||
			    functions.All(function => IsSameExternalSignature(_builder.Functions[function].Signature, first)))
				continue;
			
			foreach (var function in byModule.Where(static m => m.Count() == 1).SelectMany(static m => m))
				Diagnostics.Add(new(DiagnosticSeverity.Error, GetIdentifier(function.Syntax).SourceLocation,
					"'ext' functions cannot be overloaded"));
		}
	}
	
	private static bool IsSameExternalSignature(FunctionSignature first, FunctionSignature second) =>
		HasSameParameters(first, second) && first.ReturnType == second.ReturnType &&
		first.ParameterModes.SequenceEqual(second.ParameterModes);
	
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
				if (path.Children.ContainsKey(name))
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
		first.IsVariadic == second.IsVariadic && first.ParameterTypes.Length == second.ParameterTypes.Length &&
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