using System.Text;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	public IResolvedExpressionNode Visit(AccessExpressionNode node)
	{
		if (FindTraitView(node.Target) is var (trait, view))
			return VisitTraitMember(node, trait, view);
		
		if (ResolveEnumType(node.Target) is { } enumType)
		{
			ProbeMember(node, () => GetStaticCandidates(enumType));
			return DeclaresNonCaseMember(enumType, node.Member.Text)
				? VisitStaticMember(node, RequireTypeArguments(enumType, node.Target))
				: VisitEnumCase(node, enumType, null);
		}
		
		if (CurrentResolutionContext.ResolveModule(node.Target) is { } module)
		{
			ProbeMember(node, () => GetModuleCandidates(module));
			return VisitModuleMember(node, module);
		}
		
		if (CurrentResolutionContext.TryResolveExpressionAsType(node.Target) is { } type)
		{
			ProbeMember(node, () => GetStaticCandidates(type));
			return VisitStaticMember(node, RequireTypeArguments(type, node.Target));
		}
		
		return ResolveAccess(node, VisitNode(node.Target));
	}
	
	private IResolvedExpressionNode VisitModuleMember(AccessExpressionNode node, ModulePathSymbol module)
	{
		var context = CurrentResolutionContext;
		if (context.ResolveMember(module, node.Member.Text) is { } member)
			return ResolveSymbolValue(node, member);
		
		var diagnostic = context.ReportUndefinedMember(node.Member.SourceLocation, module, node.Member.Text);
		return Error(node, diagnostic, CurrentTargetType);
	}
	
	private IResolvedExpressionNode ResolveAccess(AccessExpressionNode node, IResolvedExpressionNode target)
	{
		if (IsInvalid(target))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (target is ResolvedLiteralExpressionNode { Type: UntypedType } or ResolvedInterpolatedStringExpressionNode)
			target = MaterializeAsDefault(target);
		
		if (IsInvalid(target))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		target = Decay(target);
		var valueType = target.Type;
		ProbeMember(node, () => GetValueMemberCandidates(valueType));
		if (GetMemberOwner(target.Type, node.Member.Text) != target.Type)
			target = ResolveDereference(TokenType.OpStar, target, node.Target);
		
		var memberName = node.Member.Text;
		var resolutionContext = CurrentResolutionContext;
		if (GetPropertyMember(target.Type, memberName) is { } property)
			return IsAmbiguousProperty(target.Type, memberName)
				? Error(node, $"Reference to '{memberName}' is ambiguous", CurrentTargetType,
					node.Member.SourceLocation)
				: ResolvePropertyUse(node, node.Member.SourceLocation, property, target.Type, target);
		
		if (resolutionContext.TypePool.ResolveMember(target.Type, memberName) is not { } member)
			return Error(node, FindFunctions(target.Type, memberName) switch
			{
				[] => DescribeMissingMember(target.Type, memberName),
				var functions when functions.Any(static function => function.HasReceiver) =>
					"Cannot use methods as values",
				_ => "Cannot use static functions through values"
			}, CurrentTargetType, node.Member.SourceLocation);
		
		if (member is FieldSymbol field && !CanAccess(target.Type, field.Visibility))
			return Error(node, DiagnosticReporter.ReportHidden(node.Member.SourceLocation, memberName, field.Visibility,
				true), CurrentTargetType);
		
		var memberType = GetMemberType(member);
		return new ResolvedAccessExpressionNode(target, member, memberType, node);
	}
	
	public IResolvedExpressionNode Visit(ArrayExpressionNode node)
	{
		var elementType = ExpectedType switch
		{
			ArrayType array => array.ElementType,
			{ } expected => FindConvertedElementType(expected, node.Values.Length),
			null => null
		};
		
		var visited = new IResolvedExpressionNode?[node.Values.Length];
		for (var i = 0; i < node.Values.Length; i++)
		{
			if (elementType is not null || !IsCaseNameSyntax(node.Values[i]))
				visited[i] = VisitNode(node.Values[i], elementType);
		}
		
		var peers = visited.OfType<IResolvedExpressionNode>().Select(static value => value.Type).ToList();
		if (survey is not null && peers.Count == 0 && node.Values.Length > 0)
			return new ResolvedArrayExpressionNode(_typePool.GetArrayType(CaseNameType.Instance, node.Values.Length),
				[..node.Values.Select(value => CreateCaseName(GetCaseToken(value)!.Value, value))], node);
		
		var values = node.Values
			.Select((expression, i) => visited[i] ?? VisitCaseName(expression, peers) ?? VisitNode(expression, null))
			.ToList();
		
		if (values.Count == 0)
		{
			if (elementType is null && ExpectedType is not InvalidType)
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, "Cannot infer the type of '[]'"));
			
			elementType ??= NativeSymbols.Invalid;
		}
		else if (elementType is not null)
		{
			for (var i = 0; i < values.Count; i++)
				values[i] = CoerceToType(values[i], elementType);
		}
		else
		{
			elementType = UnifyTypes(values) ?? UnifyRows(node, values);
			if (elementType is null)
			{
				if (!values.Any(static value => value.Type is InvalidType))
				{
					Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
						HoldsDistinctClosures(values.Select(static v => v.Type))
							? "The elements of this array hold different 'own fun' closures"
							: $"The elements of this array have incompatible types: {JoinTypeNames(values)}"));
				}
				
				elementType = NativeSymbols.Invalid;
			}
			
			for (var i = 0; i < values.Count; i++)
				values[i] = CoerceToType(values[i], elementType);
		}
		
		if (elementType is DynType dyn)
			return Error(node, $"Cannot use '{dyn.Name}' by value", null);
		
		var type = _typePool.GetArrayType(elementType, node.Values.Length);
		return new ResolvedArrayExpressionNode(type, values, node);
	}
	
	private TypeSymbol? UnifyRows(ArrayExpressionNode node, List<IResolvedExpressionNode> values)
	{
		if (!node.Values.All(static value => value is ArrayExpressionNode))
			return null;
		
		TypeSymbol? common = values[0].Type;
		for (var i = 1; i < values.Count && common is not null; i++)
			common = FindCommonRowType(common, values[i].Type);
		
		if (common is null)
			return null;
		
		for (var i = 0; i < values.Count; i++)
		{
			if (values[i].Type != common)
				values[i] = VisitNode(node.Values[i], common);
		}
		
		return common;
	}
	
	private TypeSymbol? FindCommonRowType(TypeSymbol a, TypeSymbol b) =>
		a is ArrayType { LengthParameter: null } left && b is ArrayType { LengthParameter: null } right
			? left.Length == right.Length && FindCommonRowType(left.ElementType, right.ElementType) is { } element
				? _typePool.GetArrayType(element, left.Length)
				: null
			: FindCommonType(a, b);
	
	private TypeSymbol? FindConvertedElementType(TypeSymbol target, int length)
	{
		var elementTypes = GetInwardConversions(target)
			.Where(static info => info.Signature.GetMode(0) != ParameterMode.Mut)
			.Where(info => info.Signature.GetDeclaredType(0) is ArrayType source &&
			               (source.LengthParameter is not null || source.Length == length) &&
			               !TypePool.FindTypeParameters(source.ElementType)
				               .Intersect(GetOpenTypeParameters(info))
				               .Any())
			.Select(static info => ((ArrayType)info.Signature.GetDeclaredType(0)).ElementType)
			.Distinct()
			.ToList();
		
		return elementTypes is [var only] ? only : null;
	}
	
	public IResolvedExpressionNode Visit(InterpolatedStringExpressionNode node) =>
		new ResolvedInterpolatedStringExpressionNode(node.Values.Select(value => VisitNode(value, null)), node);
	
	private IResolvedExpressionNode FoldInterpolation(ResolvedInterpolatedStringExpressionNode node)
	{
		if (_foldedStrings.TryGetValue(node, out var folded))
			return folded;
		
		if (node.Values.FirstOrDefault(static value => value.Type is NeverType) is { } endless)
			return endless;
		
		var syntax = node.Interpolation;
		var text = new StringBuilder(syntax.Segments[0]);
		var isValid = true;
		for (var i = 0; i < node.Values.Length; i++)
		{
			if (FoldString(node.Values[i]) is { } value)
				text.Append(value);
			else
				isValid = false;
			
			if (syntax.Specs[i].Length > 0)
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, syntax.SpecLocations[i],
					"Cannot use format specs in constant strings"));
				
				isValid = false;
			}
			
			text.Append(syntax.Segments[i + 1]);
		}
		
		folded = isValid
			? new ResolvedLiteralExpressionNode(NativeSymbols.UntypedString, text.ToString(), syntax)
			: new ResolvedInvalidExpressionNode(syntax);
		
		_foldedStrings[node] = folded;
		return folded;
	}
	
	private bool HolesConform(ResolvedInterpolatedStringExpressionNode node, FStrType type)
	{
		var target = _typePool.GetBorrowType(type.Value, false);
		return node.Values.All(hole => IsInvalid(hole) || hole.Type == target ||
		                               CanConvertToDyn(
			                               hole.Type is UntypedType ? GetDefaultType(hole) : Decay(hole).Type,
			                               target));
	}
	
	private IResolvedExpressionNode CreateTemplate(ResolvedInterpolatedStringExpressionNode node, FStrType type)
	{
		if (_templates.TryGetValue((node, type), out var existing))
			return existing;
		
		var syntax = node.Interpolation;
		var target = _typePool.GetBorrowType(type.Value, false);
		var values = new List<IResolvedExpressionNode>(node.Values.Length);
		foreach (var hole in node.Values)
		{
			var value = hole.Type is UntypedType ? MaterializeAsDefault(hole) : hole;
			if (IsInvalid(value))
				continue;
			
			if (value.Type == target)
				values.Add(value);
			else if (ConvertToDyn(value, target) is { } converted)
				values.Add(converted);
			else
				Diagnostics.Add(new(DiagnosticSeverity.Error, hole.Syntax.SourceLocation,
					$"'{Decay(value).Type.Name}' doesn't implement '{type.Value.TraitName}'"));
		}
		
		var texts = new List<string> { syntax.Segments[0] };
		for (var i = 0; i < syntax.Specs.Length; i++)
		{
			texts.Add(syntax.Specs[i]);
			texts.Add(syntax.Segments[i + 1]);
		}
		
		IResolvedExpressionNode template = values.Count == node.Values.Length
			? new ResolvedFStrExpressionNode(type, texts, values, null, syntax)
			: new ResolvedInvalidExpressionNode(syntax, type);
		
		_templates[(node, type)] = template;
		return template;
	}
	
	private ResolvedFStrExpressionNode CreateTextTemplate(IResolvedExpressionNode text, FStrType type) =>
		_evaluator.Evaluate(text) is StringConstant constant
			? new ResolvedFStrExpressionNode(type, [constant.Text], [], null, text.Syntax)
			: new ResolvedFStrExpressionNode(type, [], [], text, text.Syntax);
	
	private string? FoldString(IResolvedExpressionNode value)
	{
		if (value is ResolvedInterpolatedStringExpressionNode interpolation)
			value = FoldInterpolation(interpolation);
		
		if (IsInvalid(value))
			return null;
		
		if (!IsString(value.Type))
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, value.Syntax.SourceLocation,
				$"'{value.Type.Name}' is not a string"));
			
			return null;
		}
		
		switch (_evaluator.Evaluate(value))
		{
			case StringConstant constant:
				return constant.Text;
			
			case InvalidConstant:
				return null;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, value.Syntax.SourceLocation,
					$"'{value.Syntax.SourceLocation.GetText()}' isn't a constant"));
				
				return null;
		}
	}
	
	private static bool IsString(TypeSymbol type) => type is StringType or UntypedStringType or InterpolatedStringType;
	
	private IResolvedExpressionNode ConcatenateStrings(IExpressionNode node, Token op, IResolvedExpressionNode left,
		IResolvedExpressionNode right)
	{
		if (left.Type is StringType && right.Type is StringType && left.Type != right.Type)
			return Error(node,
				DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, op, right, node.SourceLocation),
				CurrentTargetType);
		
		var leftText = FoldString(left);
		var rightText = FoldString(right);
		if (leftText is null || rightText is null)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		return new ResolvedLiteralExpressionNode(NativeSymbols.UntypedString, leftText + rightText, node);
	}
	
	public IResolvedExpressionNode Visit(UndefExpressionNode node)
	{
		var type = node.Type is null
			? CurrentTargetType ?? UntypedUndefType.Instance
			: CurrentResolutionContext.ResolveType(node.Type);
		
		return new ResolvedUndefExpressionNode(type, node);
	}
	
	public IResolvedExpressionNode Visit(MissingExpressionNode node) =>
		new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	
	public IResolvedExpressionNode Visit(SizeOfExpressionNode node) => ResolveLayout(node, node.Target, false);
	
	public IResolvedExpressionNode Visit(AlignOfExpressionNode node) => ResolveLayout(node, node.Target, true);
	
	private IResolvedExpressionNode ResolveLayout(IExpressionNode node, IExpressionNode targetNode, bool isAlignment)
	{
		TypeSymbol target;
		if (CurrentResolutionContext.TryResolveExpressionAsType(targetNode) is { } type)
			target = RequireTypeArguments(type, targetNode);
		else
		{
			var value = Decay(MaterializeAsDefault(VisitNode(targetNode)));
			if (value.Type is DynType)
			{
				var borrow = _typePool.GetBorrowType(value.Type, false);
				return new ResolvedConversionExpressionNode(new ResolvedBorrowExpressionNode(value, borrow, true,
					value.Syntax), new DynLayoutConversion(borrow, isAlignment), node);
			}
			
			target = value.Type;
		}
		
		if (TypePool.FindValueDyn(target) is { } dyn)
			return Error(node, $"Cannot use '{dyn.Name}' by value", CurrentTargetType);
		
		if (TypePool.ContainsTypeParameters(target))
			return isAlignment
				? new ResolvedAlignOfExpressionNode(target, node)
				: new ResolvedSizeOfExpressionNode(target, node);
		
		if (target is NeverType)
			return Error(node, $"'{target.Name}' has no {(isAlignment ? "alignment" : "size")}", CurrentTargetType);
		
		if (IsInvalid(target) || _typePool.SizeTable.TryGetSize(target) is not { } size)
			return new ResolvedInvalidExpressionNode(node);
		
		var bits = isAlignment ? size.CountAlignmentBits(_pointerBitSize) : size.CountBits(_pointerBitSize);
		return new ResolvedLiteralExpressionNode(NativeSymbols.UIntSize, (bits + 7) / 8, node);
	}
	
	public IResolvedExpressionNode Visit(TypeExpressionNode node) =>
		Error(node, "Cannot use types as values", CurrentTargetType);
	
	public IResolvedExpressionNode Visit(NameOfExpressionNode node)
	{
		if (!IsDefinedName(node.Name))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var name = node.Name switch
		{
			AccessExpressionNode access => access.Member.Text,
			VarExpressionNode variable => variable.Identifier.Text,
			_ => throw new InvalidOperationException()
		};
		
		return new ResolvedLiteralExpressionNode(NativeSymbols.UntypedString, name, node);
	}
	
	private bool IsDefinedName(IExpressionNode name)
	{
		var context = CurrentResolutionContext;
		switch (name)
		{
			case VarExpressionNode variable when context.Resolve(variable.Identifier.Text) is null &&
			                                     CurrentScope?.ResolveLabel(variable.Identifier.Text) is null:
				Diagnostics.Add(ReportUndefinedSymbol(variable, variable.Identifier.Text));
				return false;
			
			case VarExpressionNode:
				return true;
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				if (context.ResolveMember(module, access.Member.Text) is not null)
					return true;
				
				Diagnostics.Add(context.ReportUndefinedMember(access.Member.SourceLocation, module,
					access.Member.Text));
				
				return false;
			
			case AccessExpressionNode access when context.TryResolveExpressionAsType(access.Target) is { } type:
				return type is EnumSymbol enumType && enumType.GetStaticField(access.Member.Text) is null &&
				       GetPropertyMember(enumType, access.Member.Text) is null
					? FindCase(enumType, access.Member) is not null
					: HasMember(type, access.Member);
			
			case AccessExpressionNode access:
				var target = Decay(VisitNode(access.Target, null));
				return !IsInvalid(target) && HasMember(GetMemberOwner(target.Type, access.Member.Text), access.Member);
			
			default:
				return false;
		}
	}
	
	private bool HasMember(TypeSymbol type, Token member)
	{
		if (type.GetStaticField(member.Text) is { } global)
			return IsAccessibleMember(type, member, global.Visibility);
		
		if (GetPropertyMember(type, member.Text) is { } property)
			return IsAccessibleMember(type, member, property.Visibility);
		
		if (GetMethods(type, member.Text).Select(static method => method.Function).ToList() is [_, ..] functions)
		{
			if (functions.Any(function => CanAccess(type, function)))
				return true;
			
			Diagnostics.Add(ReportHiddenMember(member.SourceLocation, member.Text, functions));
			return false;
		}
		
		switch (_typePool.ResolveMember(type, member.Text))
		{
			case FieldSymbol field when !CanAccess(type, field.Visibility):
				Diagnostics.Add(DiagnosticReporter.ReportHidden(member.SourceLocation, member.Text, field.Visibility,
					true));
				
				return false;
			
			case not null:
				return true;
		}
		
		Diagnostics.Add(DescribeAbsentMember(type, member.Text).At(member.SourceLocation));
		return false;
	}
	
	private bool IsAccessibleMember(TypeSymbol type, Token member, Visibility visibility)
	{
		if (CanAccess(type, visibility))
			return true;
		
		Diagnostics.Add(DiagnosticReporter.ReportHidden(member.SourceLocation, member.Text, visibility, true));
		return false;
	}
}