using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void CompleteProperty(PropertySymbol property)
	{
		var node = property.Node!;
		var type = node.Type is { } typeNode ? GetPropertyType(property, typeNode) : null;
		if (node.Keyword is not null)
			ReportMissingAccessors(property);
		
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(node.Accessors.Select(static a => a.Identifier),
			name => $"'{name}' is declared more than once in '{property.Name}'"));
		
		if (node.Accessors.Select(static accessor => accessor.Receiver is null).Distinct().Count() > 1)
			Diagnostics.AddRange(node.Accessors.Select(static accessor => new Diagnostic(DiagnosticSeverity.Error,
				accessor.Identifier.SourceLocation, "Cannot combine static and instance accessors")));
		
		foreach (var accessor in node.Accessors)
		{
			var info = GetFunctionInfo((FunctionSymbol)_symbolTable.DeclarationSymbols[accessor]);
			ReportAccessor(accessor, info.Signature, type, node.Type);
		}
	}
	
	private void ReportMissingAccessors(PropertySymbol property)
	{
		var missing = (property.Getter, property.Setter) switch
		{
			(null, null) => "a getter and a setter",
			(null, _) => "a getter",
			(_, null) => "a setter",
			_ => null
		};
		
		if (missing is not null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, property.Node!.Identifier.SourceLocation,
				$"'{property.Name}' needs {missing}"));
	}
	
	private void ReportAccessor(FunctionNode accessor, FunctionSignature signature, TypeSymbol? type,
		ITypeNode? shared)
	{
		var isGetter = accessor.Identifier.Type == TokenType.KeywordGet;
		if (accessor.Receiver is { Mode: { Type: TokenType.KeywordOwn } } receiver)
			Diagnostics.Add(new(DiagnosticSeverity.Error, receiver.SourceLocation,
				$"Cannot take 'own self' in {(isGetter ? "getters" : "setters")}"));
		
		if (!isGetter && accessor.Parameters is [{ Mode: { Type: TokenType.KeywordMut } mode }])
			Diagnostics.Add(new(DiagnosticSeverity.Error, mode.SourceLocation,
				"Cannot take 'mut' parameters in setters"));
		
		if (type is null or InvalidType)
			return;
		
		if (isGetter && accessor.ReturnType is { } returnType && returnType != shared &&
		    signature.ReturnType is not InvalidType && signature.ReturnType != type)
			Diagnostics.Add(new(DiagnosticSeverity.Error, returnType.SourceLocation,
				$"'get' must return '{type.Name}'"));
		
		if (isGetter || accessor.Parameters is not [var value] || value.Type == shared)
			return;
		
		var valueType = signature.GetDeclaredType(signature.ParameterTypes.Length - 1);
		if (valueType is not InvalidType && valueType != type)
			Diagnostics.Add(new(DiagnosticSeverity.Error, value.Type.SourceLocation,
				$"'{value.Identifier.Text}' must be '{type.Name}'"));
	}
	
	private TypeSymbol GetPropertyType(PropertySymbol property, ITypeNode typeNode)
	{
		if (_propertyTypes.TryGetValue(property, out var type))
			return type;
		
		var context = _declarations[property].Context;
		var owner = GetVisibilityOwner(context)!;
		type = RejectValueDyn(typeNode, context.ResolveType(typeNode));
		_propertyTypes[property] = type;
		
		var visibility = property.Node!.Accessors
			.Select(accessor => (FunctionSymbol)_symbolTable.DeclarationSymbols[accessor])
			.Select(accessor => GetEffectiveVisibility(accessor.Visibility, owner))
			.DefaultIfEmpty(GetEffectiveVisibility(property.Visibility, owner))
			.Max();
		
		ReportHiddenType(typeNode, type, visibility, property.Name);
		return type;
	}
}
