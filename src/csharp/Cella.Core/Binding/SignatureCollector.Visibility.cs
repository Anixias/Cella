using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private TypeSymbol ResolveSignatureType(FunctionSymbol function, ITypeNode node, ResolutionContext context,
		bool isReturn = false) =>
		function.Property is { Node.Type: { } shared } property && node == shared
			? GetPropertyType(property, shared)
			: isReturn
				? context.ResolveReturnType(node)
				: context.ResolveType(node);
	
	private TypeSymbol RejectValueDyn(ITypeNode node, TypeSymbol type, ParameterMode mode = ParameterMode.Own)
	{
		if (type is DynType && mode != ParameterMode.Own || TypePool.FindValueDyn(type) is not { } dyn)
			return type;
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"Cannot use '{dyn.Name}' by value"));
		return NativeSymbols.Invalid;
	}
	
	private void ReportHiddenType(ITypeNode node, TypeSymbol type, Visibility visibility, string name)
	{
		if (FindHiddenType(type, visibility) is { } hidden)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"'{hidden.Name}' is less visible than '{name}'"));
	}
	
	private static TypeSymbol? FindHiddenType(TypeSymbol type, Visibility visibility) => type switch
	{
		DynType { Instance: { } trait } dyn =>
			trait.Trait.Visibility < visibility ? dyn : FindHiddenArgument(trait, visibility),
		FStrType { Value.Instance: { } trait } fstr =>
			trait.Trait.Visibility < visibility ? fstr : FindHiddenArgument(trait, visibility),
		TraitType trait => trait.Trait.Visibility < visibility ? trait : FindHiddenArgument(trait, visibility),
		PointerType pointer => FindHiddenType(pointer.BaseType, visibility),
		BorrowType borrow => FindHiddenType(borrow.Target, visibility),
		ArrayType array => FindHiddenType(array.ElementType, visibility),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType)
			.Select(part => FindHiddenType(part, visibility))
			.FirstOrDefault(static hidden => hidden is not null),
		NamedTypeSymbol { IsGenericInstance: true } instance => FindHiddenType(instance.Definition, visibility) ??
		                                                        instance.TypeArguments
			                                                        .Select(argument =>
				                                                        FindHiddenType(argument, visibility))
			                                                        .FirstOrDefault(static hidden =>
				                                                        hidden is not null),
		IExportable exportable when exportable.Visibility < visibility => type,
		_ => null
	};
	
	private static TypeSymbol? FindHiddenArgument(TraitType trait, Visibility visibility) => trait.Arguments
		.Select(argument => FindHiddenType(argument, visibility))
		.FirstOrDefault(static hidden => hidden is not null);
	
	private static Visibility GetEffectiveVisibility(Visibility member, Symbol? owner)
	{
		if (owner is null)
			return member;
		
		var ownerVisibility = ModuleIndex.GetVisibility(owner);
		return member < Visibility.Project && member < ownerVisibility ? member : ownerVisibility;
	}
	
	private static Visibility GetOwnerVisibility(Symbol owner, ResolutionContext context) =>
		owner is FunctionSymbol function
			? GetEffectiveVisibility(function, context)
			: ModuleIndex.GetVisibility(owner);
	
	private static Visibility GetEffectiveVisibility(FunctionSymbol function, ResolutionContext context) =>
		GetEffectiveVisibility(function.Visibility, GetVisibilityOwner(context));
	
	private static Symbol? GetVisibilityOwner(ResolutionContext context) =>
		context.ImplBlock is { IsExtension: true } ? null : (Symbol?)context.Trait ?? context.ContainingType;
	
	private static Diagnostic ReportStoredBorrow(ITypeNode type, Token name, IEnumerable<Token> modifiers,
		string kinds, string keyword)
	{
		var prefix = string.Concat(modifiers.Select(static modifier => $"{modifier.Text} "));
		return new(DiagnosticSeverity.Error, type.SourceLocation, $"Cannot store borrows in {kinds} that aren't 'ref'")
		{
			Hints = [$"Did you mean '{name.Text}: {prefix}ref {keyword}'?"]
		};
	}
}