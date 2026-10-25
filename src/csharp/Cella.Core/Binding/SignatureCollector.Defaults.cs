using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private readonly Dictionary<TypeParameterSymbol, PendingDefault> _pendingDefaults = [];
	private readonly HashSet<TypeParameterSymbol> _resolvingDefaults = [];
	
	private readonly List<(TypeParameterSymbol Parameter, PendingDefault Pending, SourceLocation Location)>
		_defaultChecks = [];
	
	private sealed record PendingDefault
	(
		TypeParameterNode Node,
		ImmutableArray<TypeParameterSymbol> Parameters,
		int Index,
		ImmutableHashSet<TypeParameterSymbol> EarlierDerived,
		Func<ResolutionContext> Context,
		ErrorContainment.Owner Owner
	);
	
	private void RegisterDefaults(FileNode node)
	{
		foreach (var declaration in node.Declarations)
		{
			using var frame = Own(declaration);
			var symbol = _symbolTable.DeclarationSymbols[declaration];
			var context = _declarations[symbol].Context;
			switch (declaration)
			{
				case RecordNode record:
					AddDefaults(record.TypeParameters, ((RecordSymbol)symbol).TypeParameters,
						() => context with { ContainingType = (RecordSymbol)symbol });
					
					AddMemberDefaults(record.Members);
					break;
				
				case EnumNode enumNode:
					AddDefaults(enumNode.TypeParameters, ((EnumSymbol)symbol).TypeParameters,
						() => context with { ContainingType = (EnumSymbol)symbol });
					
					AddMemberDefaults(enumNode.Members);
					break;
				
				case FunctionNode function:
					AddDefaults(function.TypeParameters, ((FunctionSymbol)symbol).DeclaredTypeParameters,
						() => _declarations[symbol].Context);
					
					break;
				
				case TraitNode traitNode:
					AddDefaults(traitNode.TypeParameters, ((TraitSymbol)symbol).TypeParameters,
						InTrait(context, (TraitSymbol)symbol));
					
					AddMemberDefaults(traitNode.Members);
					break;
				
				case ImplNode implNode:
					foreach (var parameter in implNode.TypeParameters.Concat(
						         TypeParameterNode.CollectNestedLeaves(implNode.TypeParameters)))
						if (GetDefaultLocation(parameter) is { } location)
							Diagnostics.Add(new(DiagnosticSeverity.Error, location,
								"Cannot declare defaults in impl blocks"));
					
					AddMemberDefaults(implNode.Members);
					break;
			}
		}
	}
	
	private static Func<ResolutionContext> InTrait(ResolutionContext context, TraitSymbol trait) =>
		() => context with { ContainingType = trait.Self, Trait = trait };
	
	private void AddMemberDefaults(IEnumerable<IDeclarationNode> members)
	{
		foreach (var member in members.OfType<FunctionNode>())
		{
			using var frame = Own(member);
			var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[member];
			AddDefaults(member.TypeParameters, function.DeclaredTypeParameters, () => _declarations[function].Context);
		}
	}
	
	private void AddDefaults(ImmutableArray<TypeParameterNode> nodes, ImmutableArray<TypeParameterSymbol> parameters,
		Func<ResolutionContext> context)
	{
		foreach (var nested in nodes.Where(static node => !node.Nested.IsEmpty))
			Diagnostics.Add(new(DiagnosticSeverity.Error, nested.Identifier.SourceLocation,
				"Cannot declare nested type parameters outside impl blocks"));
		
		var hasDefaults = false;
		for (var i = 0; i < nodes.Length && i < parameters.Length; i++)
		{
			if (GetDefaultLocation(nodes[i]) is null)
			{
				if (hasDefaults)
					Diagnostics.Add(new(DiagnosticSeverity.Error, nodes[i].Identifier.SourceLocation,
						"Cannot take parameters without defaults after parameters with defaults"));
				
				continue;
			}
			
			hasDefaults = true;
			_pendingDefaults[parameters[i]] =
				new(nodes[i], parameters, i, FindIntroduced(nodes.Take(i), parameters), context, CurrentOwner);
		}
	}
	
	private static ImmutableHashSet<TypeParameterSymbol> FindIntroduced(IEnumerable<TypeParameterNode> nodes,
		ImmutableArray<TypeParameterSymbol> parameters)
	{
		var earlier = nodes.ToList();
		var names = TypeParameterNode.CollectBareNames(earlier)
			.Concat(TypeParameterNode.CollectDeclared(earlier).Select(static node => node.Identifier))
			.Select(static name => name.Text)
			.ToHashSet();
		
		return [..parameters.Where(parameter => parameter.IsDerived && names.Contains(parameter.Name))];
	}
	
	private static SourceLocation? GetDefaultLocation(TypeParameterNode node) =>
		node.DefaultType?.SourceLocation ?? node.DefaultValue?.SourceLocation;
	
	private void ResolveDefaults()
	{
		foreach (var parameter in _pendingDefaults.Keys.ToList())
			ResolveDefault(parameter);
	}
	
	private TypeSymbol? ResolveDefault(TypeParameterSymbol parameter)
	{
		if (parameter.DefaultArgument is { } resolved)
			return resolved;
		
		if (!_pendingDefaults.TryGetValue(parameter, out var pending))
			return null;
		
		if (!_resolvingDefaults.Add(parameter))
			return NativeSymbols.Invalid;
		
		using var frame = Own(pending.Owner);
		var location = GetDefaultLocation(pending.Node)!.Value;
		var context = pending.Context() with { DeferConstraintCheck = DeferAlways };
		var fallback = context.ResolveDefaultArgument(pending.Node, parameter);
		if (pending.Parameters.Skip(pending.Index)
			    .FirstOrDefault(later => !pending.EarlierDerived.Contains(later) && Mentions(fallback, later)) is
		    { } later)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, later == parameter
				? $"Cannot use '{later.Name}' in its own default"
				: $"Cannot use '{later.Name}' in defaults of earlier parameters"));
			
			fallback = NativeSymbols.Invalid;
		}
		else if (fallback is not InvalidType)
			_defaultChecks.Add((parameter, pending, location));
		
		parameter.DefaultArgument = fallback;
		return fallback;
	}
	
	private void CheckDefaults()
	{
		foreach (var (parameter, pending, location) in _defaultChecks)
		{
			var parameters = pending.Parameters;
			var inferred = FixOtherParameters(parameters, pending.Index);
			inferred[pending.Index] = parameter.DefaultArgument;
			new TypeInference(_typePool).InferFromBounds(parameters, inferred);
			var map = parameters.Select((other, i) => (other, inferred[i] ?? other))
				.ToDictionary(static pair => pair.other, static pair => pair.Item2);
			
			for (var i = 0; i < parameters.Length; i++)
			{
				if (i == pending.Index || map[parameters[i]] != parameters[i])
					_deferredChecks.Add((new(parameters[i], map[parameters[i]], map, location), pending.Owner));
			}
		}
		
		_defaultChecks.Clear();
	}
	
	private TypeSymbol?[] FixOtherParameters(ImmutableArray<TypeParameterSymbol> parameters, int index)
	{
		var inferred = parameters.Select((parameter, i) => i == index || parameter.IsDerived ? null : parameter)
			.ToArray<TypeSymbol?>();
		
		var open = new Queue<TypeParameterSymbol>(parameters.Where((_, i) => inferred[i] is not null));
		while (open.TryDequeue(out var parameter))
		{
			foreach (var mentioned in GetBoundTypes(parameter).SelectMany(TypePool.FindTypeParameters))
			{
				var i = parameters.IndexOf(mentioned);
				if (i < 0 || i == index || inferred[i] is not null)
					continue;
				
				inferred[i] = mentioned;
				open.Enqueue(mentioned);
			}
		}
		
		return inferred;
	}
	
	private IEnumerable<TypeSymbol> GetBoundTypes(TypeParameterSymbol parameter) => _typePool.GetBounds(parameter)
		.Concat<TypeSymbol>(_typePool.GetFunctionBounds(parameter))
		.Append(_typePool.GetEnumTag(parameter))
		.OfType<TypeSymbol>();
	
	private bool Mentions(TypeSymbol type, TypeParameterSymbol parameter) =>
		!TypePool.ContainsInvalid(type) &&
		TypePool.ContainsInvalid(_typePool.Substitute(type, new Dictionary<TypeParameterSymbol, TypeSymbol>
		{
			[parameter] = NativeSymbols.Invalid
		}));
}