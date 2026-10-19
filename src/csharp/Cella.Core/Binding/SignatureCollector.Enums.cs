using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void CompleteEnum(EnumSymbol enumType)
	{
		if (_completedTypes.Contains(enumType) || !_declarations.TryGetValue(enumType, out var declaration) ||
		    !Enter(enumType, false))
			return;
		
		var node = enumType.Node;
		var context = declaration.Context;
		var memberContext = context with { ContainingType = enumType };
		if (node.Cases.IsEmpty)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"'{enumType.Name}' needs at least one case"));
		
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(node.Cases.Select(static c => c.Identifier),
			name => $"Case '{name}' is declared more than once in '{enumType.Name}'"));
		
		ReportTypeParameters(node.TypeParameters, node.Members);
		
		var matchedType = node.MatchedType is { } matchedNode
			? ResolveMatchedType(enumType, matchedNode, memberContext)
			: null;
		
		var payloads = new Dictionary<EnumCaseSymbol, ImmutableArray<TypeSymbol>>();
		foreach (var enumCase in enumType.Cases)
		{
			Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(
				enumCase.Node.Payload.Select(static f => f.Identifier),
				name => $"Payload '{name}' is declared more than once in '{enumCase.Name}'"));
			
			if (matchedType is not null)
			{
				foreach (var field in enumCase.Fields)
					_typePool.RegisterPayloadField(field, matchedType);
				
				continue;
			}
			
			ImmutableArray<TypeSymbol> types =
			[
				..enumCase.Fields.Select(f => RejectValueDyn(f.Node!.Type, memberContext.ResolveType(f.Node!.Type)))
			];
			
			for (var i = 0; i < types.Length; i++)
			{
				_typePool.RegisterPayloadField(enumCase.Fields[i], types[i]);
				ReportHiddenType(enumCase.Fields[i].Node!.Type, types[i], enumType.Visibility, enumType.Name);
				if (!node.IsRef && !TypePool.ContainsTypeParameters(types[i]) && _typePool.HoldsBorrows(types[i]))
					Diagnostics.Add(ReportStoredBorrow(enumCase.Fields[i].Node!.Type, node.Identifier, node.Modifiers,
						"enums", "enum"));
			}
			
			payloads[enumCase] = types;
		}
		
		if (matchedType is null)
			CompletePlainEnum(enumType, context, payloads);
		else
			CompleteMatchEnum(enumType, matchedType, context);
		
		_completedTypes.Add(enumType);
		Exit();
		
		foreach (var member in node.Members)
			Complete(_symbolTable.DeclarationSymbols[member]);
		
		var functions = node.Members.OfType<FunctionNode>().ToLookup(IsModeOperator);
		ReportMemberConflicts(enumType.Name, [
			..node.Cases.Select(static c => (c.Identifier, (FunctionNode?)null, MemberKind.Case)),
			..node.Members.OfType<GlobalNode>()
				.Select(static g => (g.Identifier, (FunctionNode?)null, MemberKind.Field)),
			..node.Members.OfType<PropertyNode>()
				.Select(static p => (p.Identifier, (FunctionNode?)null, MemberKind.Property)),
			..functions[false].Select(static f => (f.Identifier, (FunctionNode?)f, MemberKind.Function))
		]);
		
		ReportModeOperators(enumType, [..functions[true]]);
		ReportOperators(enumType, functions[false]);
	}
	
	private void ReportDefaultConstructor(RecordSymbol type, IEnumerable<IDeclarationNode> members)
	{
		if (members.OfType<ConstructorNode>().FirstOrDefault(static c => c.Default is not null) is not
		    { Default: { } keyword })
			return;
		
		var missing = _typePool.GetMembers(type)
			.OfType<FieldSymbol>()
			.Where(field => !_typePool.HasDefault(field))
			.Select(static field => field.IsRequired ? $"'{field.Name}' is required" : $"'{field.Name}' has no default")
			.ToList();
		
		if (missing.Count > 0)
			Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation,
				"Cannot use 'default' when fields have no default") { Hints = [..missing] });
	}
	
	private void CompletePlainEnum(EnumSymbol enumType, ResolutionContext context,
		IReadOnlyDictionary<EnumCaseSymbol, ImmutableArray<TypeSymbol>> payloads)
	{
		var node = enumType.Node;
		foreach (var enumCase in node.Cases)
		{
			if (enumCase.Values is [_, var second, ..])
				Diagnostics.Add(new(DiagnosticSeverity.Error, second.SourceLocation,
					"Cannot list several values outside 'enum match'"));
			
			if (enumCase.Else is { } elseKeyword)
				Diagnostics.Add(new(DiagnosticSeverity.Error, elseKeyword.SourceLocation,
					"Cannot use 'else' outside 'enum match'"));
		}
		
		var declaredTag = enumType.Node.TagType is { } tagNode ? ResolveTagType(tagNode, context) : null;
		var valueType = declaredTag ?? (enumType.IsExternal ? NativeSymbols.Int32 : null);
		Enter(enumType, true);
		var values = CollectCaseValues(enumType, context, valueType);
		Exit();
		var tagType = GetTagType(enumType, valueType, values);
		ReportSharedValues(enumType, values, payloads);
		if (!enumType.IsExternal && !node.Cases.IsEmpty && !values.Contains(BigInteger.Zero))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"'{enumType.Name}' needs a case with the value 0"));
		
		_typePool.RegisterEnum(enumType, tagType, values);
	}
	
	private TypeSymbol ResolveMatchedType(EnumSymbol enumType, ITypeNode node, ResolutionContext context)
	{
		var type = context.ResolveType(node);
		if (!IsMatchable(type))
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"Cannot declare 'enum match {type.Name}'"));
			
			return NativeSymbols.Invalid;
		}
		
		ReportHiddenType(node, type, enumType.Visibility, enumType.Name);
		if (!enumType.IsRef && !TypePool.ContainsTypeParameters(type) && _typePool.HoldsBorrows(type))
			Diagnostics.Add(ReportStoredBorrow(node, enumType.Node.Identifier, enumType.Node.Modifiers, "enums",
				"enum match"));
		
		return type;
	}
	
	private static bool IsMatchable(TypeSymbol type) =>
		type is InvalidType or IntegerStorageType or TypeParameterSymbol || type == NativeSymbols.Bool ||
		TypePool.HasNull(type);
	
	private void CompleteMatchEnum(EnumSymbol enumType, TypeSymbol matchedType, ResolutionContext context)
	{
		var values = new List<ImmutableArray<BigInteger>>();
		var listings = new List<(BigInteger Value, IExpressionNode Node)>();
		foreach (var enumCase in enumType.Cases)
		{
			var node = enumCase.Node;
			if (node.Else is { } elseKeyword && enumCase.Fields.IsEmpty)
				Diagnostics.Add(new(DiagnosticSeverity.Error, elseKeyword.SourceLocation,
					"'else' cases need a payload"));
			else if (node.Else is null && node.Values.IsEmpty)
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
					$"Case '{enumCase.Name}' needs a value"));
			
			var caseValues = new List<BigInteger>();
			foreach (var expression in node.Values)
			{
				if (EvaluateMatchValue(enumCase, expression, matchedType, context) is not { } value)
					continue;
				
				caseValues.Add(value);
				listings.Add((value, expression));
			}
			
			values.Add([..caseValues]);
		}
		
		ReportMatchListings(enumType, matchedType, listings);
		_typePool.RegisterMatchEnum(enumType, matchedType, values);
	}
	
	private BigInteger? EvaluateMatchValue(EnumCaseSymbol enumCase, IExpressionNode expression,
		TypeSymbol matchedType, ResolutionContext context)
	{
		var isNull = expression is LiteralExpressionNode { Token.Type: TokenType.KeywordNull };
		if (matchedType is not (IntegerStorageType or InvalidType) && matchedType != NativeSymbols.Bool)
		{
			if (isNull && TypePool.HasNull(matchedType))
				return BigInteger.Zero;
			
			Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation, isNull
				? $"'{matchedType.Name}' has no null"
				: $"Only 'null' can be listed for '{matchedType.Name}'"));
			
			return null;
		}
		
		if (isNull)
		{
			if (matchedType is not InvalidType)
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"'{matchedType.Name}' has no null"));
			
			return null;
		}
		
		var value = constants!.ResolveInitializer(expression, matchedType, context);
		switch (EvaluateChecked(value))
		{
			case IntegerConstant constant:
				return constant.Value;
			
			case BoolConstant flag:
				return flag.Value ? BigInteger.One : BigInteger.Zero;
			
			case null:
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"The value of '{enumCase.Name}' must be a constant"));
				
				return null;
			
			default:
				return null;
		}
	}
	
	private void ReportMatchListings(EnumSymbol enumType, TypeSymbol matchedType,
		IReadOnlyList<(BigInteger Value, IExpressionNode Node)> listings)
	{
		foreach (var sameValue in listings.GroupBy(static listing => listing.Value).Where(static g => g.Count() > 1))
		{
			var message = $"{DescribeMatchValue(sameValue.Key, matchedType)} is listed more than once";
			Diagnostics.AddRange(sameValue.Select(listing =>
				new Diagnostic(DiagnosticSeverity.Error, listing.Node.SourceLocation, message)));
		}
		
		var elseKeywords = enumType.Node.Cases.Select(static c => c.Else).OfType<Token>().ToList();
		if (elseKeywords.Count > 1)
			Diagnostics.AddRange(elseKeywords.Select(keyword => new Diagnostic(DiagnosticSeverity.Error,
				keyword.SourceLocation, $"'{enumType.Name}' can have only one 'else' case")));
		else if (elseKeywords.Count == 0 && matchedType is not InvalidType &&
		         !CoversEveryValue(matchedType, listings.Select(static listing => listing.Value).ToHashSet()))
			Diagnostics.Add(new(DiagnosticSeverity.Error, enumType.Node.Identifier.SourceLocation,
				$"'{enumType.Name}' needs an 'else' case"));
	}
	
	private static string DescribeMatchValue(BigInteger value, TypeSymbol matchedType) => matchedType switch
	{
		IntegerStorageType => value.ToString(),
		_ when matchedType == NativeSymbols.Bool => value.IsZero ? "false" : "true",
		_ => "null"
	};
	
	private bool CoversEveryValue(TypeSymbol matchedType, IReadOnlySet<BigInteger> values) => matchedType switch
	{
		IntegerStorageType => values.Count == BigInteger.One << (int)_typePool.SizeTable.GetSize(matchedType)
			.CountBits(_pointerBitSize),
		_ when matchedType == NativeSymbols.Bool => values.Count == 2,
		_ => false
	};
	
	private ImmutableArray<BigInteger> CollectCaseValues(EnumSymbol enumType, ResolutionContext context,
		IntegerType? valueType)
	{
		var scope = new Scope();
		var values = ImmutableArray.CreateBuilder<BigInteger>(enumType.Cases.Length);
		var next = BigInteger.Zero;
		foreach (var enumCase in enumType.Cases)
		{
			var value = enumCase.Node.Value is { } expression
				? EvaluateCaseValue(enumCase, expression,
					context with
					{
						LocalScope = scope,
						ExcludedParameters = enumType.TypeParameters,
						ExclusionPlace = "case values"
					}, valueType) ?? next
				: next;
			
			values.Add(value);
			_typePool.RegisterCaseValue(enumCase, value);
			var caseType = valueType ?? NativeSymbols.Int128;
			scope.Define(new LocalVariableSymbol(enumCase.Node.Identifier, caseType, false)
			{
				ConstantValue = new IntegerConstant(caseType, value)
			});
			
			next = value + 1;
		}
		
		return values.MoveToImmutable();
	}
	
	private BigInteger? EvaluateCaseValue(EnumCaseSymbol enumCase, IExpressionNode expression,
		ResolutionContext context, IntegerType? valueType)
	{
		var value = constants!.ResolveInitializer(expression, valueType, context);
		switch (EvaluateChecked(value))
		{
			case IntegerConstant constant when valueType is null || constant.Type is UntypedType ||
			                                   constant.Type == valueType ||
			                                   _typePool.ConversionTable.FindImplicit(constant.Type, valueType) is not
				                                   null:
				return constant.Value;
			
			case null:
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"The value of '{enumCase.Name}' must be a constant"));
				
				return null;
			
			case InvalidConstant:
				return null;
			
			case { } constant when valueType is not null:
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"Cannot convert type '{constant.Type.Name}' to '{valueType.Name}'"));
				
				return null;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"The value of '{enumCase.Name}' must be an integer"));
				
				return null;
		}
	}
	
	private IntegerType GetTagType(EnumSymbol enumType, IntegerType? tagType, IReadOnlyList<BigInteger> values)
	{
		if (tagType is null)
			return SmallestTagType(values);
		
		for (var i = 0; i < values.Count; i++)
		{
			if (Evaluator.Fits(values[i], tagType))
				continue;
			
			var enumCase = enumType.Cases[i];
			var location = enumCase.Node.Value?.SourceLocation ?? enumCase.Node.Identifier.SourceLocation;
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"'{enumCase.Name}' is {values[i]}, which doesn't fit in '{tagType.Name}'"));
		}
		
		return tagType;
	}
	
	private IntegerType? ResolveTagType(ITypeNode typeNode, ResolutionContext context)
	{
		var type = context.ResolveType(typeNode);
		if (type is IntegerType tagType && NativeSymbols.PureIntegerTypes.Contains(tagType))
			return tagType;
		
		if (type is not InvalidType)
			Diagnostics.Add(new(DiagnosticSeverity.Error, typeNode.SourceLocation,
				$"'{type.Name}' isn't an integer type"));
		
		return null;
	}
	
	private IntegerType SmallestTagType(IReadOnlyList<BigInteger> values)
	{
		IntegerType[] candidates = values.Any(static value => value.Sign < 0)
			? [NativeSymbols.Int8, NativeSymbols.Int16, NativeSymbols.Int32, NativeSymbols.Int64]
			: [NativeSymbols.UInt8, NativeSymbols.UInt16, NativeSymbols.UInt32, NativeSymbols.UInt64];
		
		return candidates.FirstOrDefault(type => values.All(value => Evaluator.Fits(value, type)))
		       ?? NativeSymbols.Int128;
	}
	
	private void ReportSharedValues(EnumSymbol enumType, IReadOnlyList<BigInteger> values,
		IReadOnlyDictionary<EnumCaseSymbol, ImmutableArray<TypeSymbol>> payloads)
	{
		var groups = enumType.Cases
			.Select((enumCase, i) => (Case: enumCase, Value: values[i]))
			.GroupBy(static c => c.Value);
		
		foreach (var group in groups)
		{
			var shape = payloads[group.First().Case];
			if (group.All(c => payloads[c.Case].SequenceEqual(shape)))
				continue;
			
			var names = DiagnosticReporter.JoinNames([..group.Select(static c => c.Case.Name)]);
			foreach (var (enumCase, value) in group)
				Diagnostics.Add(new(DiagnosticSeverity.Error, enumCase.Node.Identifier.SourceLocation,
					$"{names} share the value {value} but have different payloads"));
		}
	}
}