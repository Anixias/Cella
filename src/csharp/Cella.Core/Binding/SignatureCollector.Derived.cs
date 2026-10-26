using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void DeclareDerivedParameters()
	{
		var removed = new HashSet<TypeParameterSymbol>();
		foreach (var (symbol, declaration) in _declarations)
		{
			foreach (var function in GetDerivedOwners(symbol))
				removed.UnionWith(FindReferences(GetOwnParameters(function), declaration.Context));
		}
		
		if (removed.Count == 0)
			return;
		
		foreach (var symbol in _symbolTable.DeclarationSymbols.Values)
		{
			switch (symbol)
			{
				case FunctionSymbol function:
					function.TypeParameters = [..function.TypeParameters.Where(p => !removed.Contains(p))];
					function.DeclaredTypeParameters =
						[..function.DeclaredTypeParameters.Where(p => !removed.Contains(p))];
					
					break;
				
				case NamedTypeSymbol type:
					type.RemoveTypeParameters(removed);
					break;
				
				case TraitSymbol trait:
					trait.TypeParameters = [..trait.TypeParameters.Where(p => !removed.Contains(p))];
					break;
			}
		}
		
		foreach (var (symbol, declaration) in _declarations.ToList())
		{
			if (symbol is FunctionSymbol function && !declaration.Context.TypeParameters.IsDefault)
				_declarations[symbol] = declaration with
				{
					Context = declaration.Context with { TypeParameters = function.DeclaredTypeParameters }
				};
		}
	}
	
	private void ImplyDerivedConstraints()
	{
		foreach (var symbol in _symbolTable.DeclarationSymbols.Values)
		{
			if (symbol is ImplSymbol impl)
			{
				using var implFrame = Own(_declarations[impl].Node);
				ImmutableArray<TypeParameterSymbol> declared = [..impl.FreeParameters, ..impl.TargetFreeParameters];
				_typePool.ImplySlotConstraints([..impl.TypeParameters, ..impl.FreeParameters], declared);
				AddImplicitParameters(declared);
				continue;
			}
			
			var parameters = GetOwnParameters(symbol);
			if (symbol is FunctionSymbol { Impl: not null } || !parameters.Any(static parameter => parameter.IsDerived))
				continue;
			
			using var frame = Own(_declarations.TryGetValue(symbol, out var declaration) ? declaration.Node : null);
			_typePool.ImplySlotConstraints(parameters);
			AddImplicitParameters(parameters.Where(static parameter => parameter.IsImplicit));
		}
	}
	
	private void AddImplicitParameters(IEnumerable<TypeParameterSymbol> parameters)
	{
		foreach (var parameter in parameters)
		{
			if (parameter.Definition is { } definition &&
			    _constrainedParameters.All(constrained => constrained.Parameter != parameter))
				_constrainedParameters.Add((new(definition, [], [], []), parameter, [], CurrentOwner));
		}
	}
	
	private void ImplyMemberConstraints(FunctionSymbol function)
	{
		var parameters = function.DeclaredTypeParameters;
		if (!parameters.Any(static parameter => parameter.IsDerived))
			return;
		
		_typePool.ImplySlotConstraints(parameters);
		foreach (var derived in parameters.Where(static parameter => parameter.IsDerived))
		{
			_typePool.InheritSelfConstraints(derived);
			_typePool.InheritEnumTag(derived);
			_typePool.RegisterEnumOperations(derived);
		}
	}
	
	public IEnumerable<Token> GetDerivedParameterNames(IDeclarationNode node) =>
		_symbolTable.DeclarationSymbols.GetValueOrDefault(node) is { } symbol
			? GetOwnParameters(symbol)
				.Where(static parameter => parameter.IsDerived)
				.Select(static parameter => parameter.Definition)
				.OfType<Token>()
			: [];
	
	private static IEnumerable<Symbol> GetDerivedOwners(Symbol symbol) => symbol is ImplSymbol impl
		? GetBlockFunctions(impl)
		: [symbol];
	
	private static ImmutableArray<TypeParameterSymbol> GetOwnParameters(Symbol symbol) => symbol switch
	{
		FunctionSymbol function => function.DeclaredTypeParameters,
		NamedTypeSymbol type => type.TypeParameters,
		TraitSymbol trait => trait.TypeParameters,
		_ => []
	};
	
	private static IEnumerable<TypeParameterSymbol> FindReferences(ImmutableArray<TypeParameterSymbol> parameters,
		ResolutionContext context)
	{
		var candidates = parameters.Where(static parameter => parameter.IsImplicit).ToList();
		if (candidates.Count == 0)
			return [];
		
		var outer = context with
		{
			TypeParameters = context.TypeParameters.IsDefault
				? context.TypeParameters
				: [..context.TypeParameters.Where(static parameter => !parameter.IsImplicit)]
		};
		
		return candidates.Where(candidate => outer.Resolve(candidate.Name) is not null);
	}
	
	private bool CompleteDerivedTarget(ImplSymbol impl, NamedTypeSymbol target, SourceLocation location)
	{
		if (_typePool.CompleteArguments(target.TypeParameters, impl.TargetArguments, null, out var uninferred) is not
		    { } completed)
		{
			var map = _typePool.MapKnownArguments(target.TypeParameters, impl.TargetArguments);
			for (var i = 0; i < impl.TargetArguments.Length; i++)
			{
				if (_typePool.FindConstraintViolation(target.TypeParameters[i], impl.TargetArguments[i], map) is
				    { } violation)
				{
					Diagnostics.Add(violation.At(location));
					return false;
				}
			}
			
			var missing = uninferred ?? target.TypeParameters[impl.TargetArguments.Length];
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"Cannot infer '{missing.Name}' for '{target.Name}'"));
			
			return false;
		}
		
		var written = impl.TargetArguments.Length;
		var bound = completed.Skip(written).OfType<TypeParameterSymbol>().Where(impl.FreeParameters.Contains)
			.ToHashSet();
		
		impl.TargetArguments = completed;
		impl.TypeParameters = impl.TypeParameters.InsertRange(written, completed.Skip(written).Select((argument, i) =>
			argument is TypeParameterSymbol parameter && bound.Contains(parameter)
				? parameter
				: new TypeParameterSymbol(target.TypeParameters[written + i].Name, []) { IsDerived = true }));
		
		foreach (var constructor in impl.Constructors)
			constructor.TypeParameters = impl.TypeParameters;
		
		impl.FreeParameters = [..impl.FreeParameters.Where(parameter => !bound.Contains(parameter))];
		impl.TargetFreeParameters = [..bound];
		ImmutableArray<TypeParameterSymbol> outer = impl.Node.Traits.IsEmpty
			? impl.TypeParameters
			: [..impl.TypeParameters, ..impl.FreeParameters];
		
		foreach (var function in GetBlockFunctions(impl))
		{
			function.DeclaredTypeParameters = [..function.DeclaredTypeParameters.Where(p => !bound.Contains(p))];
			function.TypeParameters = [..outer, ..function.DeclaredTypeParameters];
		}
		
		return true;
	}
	
	private void RegisterDerivedBounds(ImmutableArray<TypeParameterNode> nodes,
		ImmutableArray<TypeParameterSymbol> parameters, ResolutionContext context, Symbol? owner) =>
		RegisterBounds([..TypeParameterNode.CollectDeclared(nodes)],
			[..parameters.Where(static parameter => parameter is { IsDerived: true, IsImplicit: false })], context,
			owner);
}