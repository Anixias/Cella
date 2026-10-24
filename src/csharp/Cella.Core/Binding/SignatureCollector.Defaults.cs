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
	
	private sealed record PendingDefault
	(
		TypeParameterNode Node,
		ImmutableArray<TypeParameterSymbol> Parameters,
		int Index,
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
		foreach (var nested in nodes.Where(static node => !node.Nested.IsEmpty)
			         .Concat(TypeParameterNode.CollectDeclared(nodes)))
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
			_pendingDefaults[parameters[i]] = new(nodes[i], parameters, i, context, CurrentOwner);
		}
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
		if (pending.Parameters.Skip(pending.Index).FirstOrDefault(later => Mentions(fallback, later)) is { } later)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, later == parameter
				? $"Cannot use '{later.Name}' in its own default"
				: $"Cannot use '{later.Name}' in defaults of earlier parameters"));
			
			fallback = NativeSymbols.Invalid;
		}
		else if (fallback is not InvalidType)
			_deferredChecks.Add((new(parameter, fallback, TypePool.CreateMap(pending.Parameters, pending.Parameters),
				location), pending.Owner));
		
		parameter.DefaultArgument = fallback;
		return fallback;
	}
	
	private bool Mentions(TypeSymbol type, TypeParameterSymbol parameter) =>
		!TypePool.ContainsInvalid(type) &&
		TypePool.ContainsInvalid(_typePool.Substitute(type, new Dictionary<TypeParameterSymbol, TypeSymbol>
		{
			[parameter] = NativeSymbols.Invalid
		}));
}