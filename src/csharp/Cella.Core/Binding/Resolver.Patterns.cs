using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private (IResolvedExpressionNode Value, EnumSymbol? Type) ResolveMatchedValue(IExpressionNode node)
	{
		var value = VisitNode(node, null);
		if (value is ResolvedOwnExpressionNode { Type: BorrowType } owned)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, "Cannot move borrowed values"));
			value = owned.Value;
		}
		
		value = Decay(value);
		if (!IsInvalid(value) && value.Type is UntypedType)
			value = MaterializeAsDefault(value);
		
		if (value.Type is EnumSymbol enumType)
			return (value, enumType);
		
		if (value.Type == NativeSymbols.Void)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, "Cannot match 'void' values"));
		
		return (value, null);
	}
	
	private IResolvedExpressionNode TakeOwnership(IResolvedExpressionNode value, IExpressionNode syntax,
		EnumSymbol? enumType, bool isMut, IEnumerable<PatternNode?> patterns)
	{
		if (isMut)
			return MakeWritable(value);
		
		if (enumType is null || !IsStored(value) || !patterns.Any(pattern => MovesPayload(pattern, enumType)))
			return value;
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, syntax.SourceLocation,
			$"Cannot move '{enumType.Name}' values implicitly"));
		
		return value;
	}
	
	private bool MovesPayload(PatternNode? pattern, TypeSymbol type)
	{
		if (pattern is null)
			return false;
		
		if (pattern.Binding is { } binding && MovesValue(binding, type))
			return true;
		
		if (type is not EnumSymbol enumType)
			return pattern.IsDestructuring && pattern.Elements.Any(element =>
				GetFieldName(element) is { } name && _typePool.ResolveMember(type, name.Text) is FieldSymbol field &&
				MovesSlot(element, GetMemberType(field)));
		
		if (pattern.Kind != PatternKind.Case ||
		    enumType.Cases.FirstOrDefault(c => c.Name == pattern.CaseName.Text) is not { } enumCase)
			return false;
		
		var payloadTypes = _typePool.GetPayloadTypes(enumType, enumCase);
		for (var i = 0; i < pattern.Elements.Length && i < payloadTypes.Length; i++)
		{
			if (MovesSlot(pattern.Elements[i], payloadTypes[i]))
				return true;
		}
		
		return false;
	}
	
	private bool MovesSlot(PatternNode element, TypeSymbol type) =>
		element.Kind == PatternKind.Binding ? MovesValue(element, type) : MovesPayload(element, type);
	
	private bool MovesValue(PatternNode binding, TypeSymbol type) =>
		binding.Mode?.Type is TokenType.KeywordOwn or TokenType.KeywordVar && !_typePool.IsCopy(type);
	
	private static Token? GetFieldName(PatternNode element) =>
		element.Field ?? (element.Kind == PatternKind.Binding ? element.CaseName : null);
	
	private bool OwnsValue(IResolvedExpressionNode value, EnumSymbol? enumType) =>
		enumType is not null && !_typePool.IsCopy(enumType) && !IsStored(value);
	
	private static bool IsStored(IResolvedExpressionNode value) => value switch
	{
		ResolvedVarExpressionNode or ResolvedGlobalExpressionNode => true,
		ResolvedAccessExpressionNode { Member: FieldSymbol } e => IsStored(e.Target),
		ResolvedIndexerExpressionNode e => IsStored(e.Target),
		ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } => true,
		ResolvedAssignmentExpressionNode e => IsStored(e.Left),
		_ => false
	};
	
	private ResolvedPattern? ResolvePattern(PatternNode pattern, EnumSymbol enumType, bool isMut, bool ownsValue)
	{
		if (pattern.TypePath.Length > 0 && !IsEnumName(pattern.TypePath, enumType))
			return null;
		
		if (FindCase(enumType, pattern.CaseName) is not { } enumCase)
			return null;
		
		var payloadTypes = _typePool.GetPayloadTypes(enumType, enumCase);
		if (pattern.Elements.Length != payloadTypes.Length || pattern.HasParentheses == payloadTypes.IsEmpty)
		{
			Diagnostics.Add(ReportPayloadCount(pattern.SourceLocation, enumType, enumCase));
			return null;
		}
		
		var bindings = new LocalVariableSymbol?[payloadTypes.Length];
		var elements = new ResolvedPattern?[payloadTypes.Length];
		for (var i = 0; i < payloadTypes.Length; i++)
		{
			var element = pattern.Elements[i];
			if (element.Field is { } field)
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, field.SourceLocation, "Cannot name payload values"));
				return null;
			}
			
			if (!ResolveSlot(element, payloadTypes[i], isMut, ownsValue, false, out bindings[i], out elements[i]))
				return null;
		}
		
		var resolved = new ResolvedPattern(enumCase, bindings) { Elements = [..elements] };
		ReportRepeatedBindings(resolved.AllBindings);
		if (enumType.IsMatch)
		{
			foreach (var binding in bindings.OfType<LocalVariableSymbol>().Where(static b => b.IsMutBinding))
				Diagnostics.Add(new(DiagnosticSeverity.Error, binding.Identifier.SourceLocation,
					$"Cannot mutably borrow '{enumType.Name}' payloads"));
		}
		
		return resolved;
	}
	
	private bool ResolveSlot(PatternNode element, TypeSymbol type, bool isMut, bool ownsValue, bool movesFields,
		out LocalVariableSymbol? binding, out ResolvedPattern? nested)
	{
		binding = null;
		nested = null;
		if (element.Kind == PatternKind.Binding)
		{
			binding = CreateBinding(element.CaseName, element.Mode, type, isMut,
				ownsValue || movesFields && MovesValue(element, type));
			
			return true;
		}
		
		if (type is DynType dyn && element.Kind is PatternKind.Case or PatternKind.Type)
		{
			nested = ResolveTypePattern(element, dyn, isMut, element.Binding);
			return nested is not null;
		}
		
		nested = ResolveArmPattern(element, type, isMut, ownsValue);
		if (nested is null)
			return false;
		
		if (element.Binding is not { } trailing)
			return true;
		
		if (trailing.Mode is null)
			ReportUntypedBinding(trailing);
		
		binding = CreateBinding(trailing.CaseName, trailing.Mode, type, isMut,
			ownsValue || movesFields && MovesValue(trailing, type));
		
		if (binding is { IsBorrowBinding: false } && _typePool.NeedsDrop(type) && nested.HasBindings)
			Diagnostics.Add(new(DiagnosticSeverity.Error, binding.Identifier.SourceLocation,
				$"Cannot move '{binding.Name}' and bind its parts"));
		
		return true;
	}
	
	private List<LocalVariableSymbol?> CreateBindings(PatternNode pattern, bool isMut, bool ownsValue)
	{
		var bindings = new List<LocalVariableSymbol?>();
		if (pattern.Binding is { } trailing)
			bindings.Add(CreateBinding(trailing.CaseName, trailing.Mode, NativeSymbols.Invalid, isMut, ownsValue));
		
		foreach (var element in pattern.Elements)
		{
			if (element.Kind == PatternKind.Binding)
				bindings.Add(CreateBinding(element.CaseName, element.Mode, NativeSymbols.Invalid, isMut, ownsValue));
			else
				bindings.AddRange(CreateBindings(element, isMut, ownsValue));
		}
		
		return bindings;
	}
	
	private ResolvedPattern? ResolveTopPattern(PatternNode syntax, TypeSymbol subjectType, bool isMut, bool ownsValue)
	{
		if (syntax.Binding is not { } binding ||
		    subjectType is DynType && syntax.Kind is PatternKind.Case or PatternKind.Type)
			return ResolveArmPattern(syntax, subjectType, isMut, ownsValue);
		
		ReportUntypedBinding(binding);
		return null;
	}
	
	private void ReportUntypedBinding(PatternNode binding) =>
		Diagnostics.Add(new(DiagnosticSeverity.Error, binding.SourceLocation,
			$"Expected a type test before '{binding.CaseName.Text}'"));
	
	private ResolvedPattern? ResolveArmPattern(PatternNode syntax, TypeSymbol subjectType, bool isMut, bool ownsValue)
	{
		ProbePattern(syntax, subjectType);
		if (IsInvalid(subjectType))
			return null;
		
		switch (syntax.Kind)
		{
			case PatternKind.Case when subjectType is EnumSymbol enumType && IsCasePattern(syntax, enumType):
				return ResolvePattern(syntax, enumType, isMut, ownsValue);
			
			case PatternKind.Case or PatternKind.Type when subjectType is DynType dyn:
				return ResolveTypePattern(syntax, dyn, isMut, syntax.Binding);
			
			case PatternKind.Case when syntax.IsDestructuring && subjectType is not EnumSymbol:
				return ResolveRecordPattern(syntax, subjectType, isMut, ownsValue);
			
			case PatternKind.Case when syntax.Value is not null:
			case PatternKind.Value or PatternKind.Range or PatternKind.Relational:
				return ResolveTestPattern(syntax, subjectType);
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, syntax.SourceLocation, "Expected a value"));
				return null;
		}
	}
	
	private bool IsCasePattern(PatternNode syntax, EnumSymbol enumType)
	{
		if (syntax.Value is null || syntax.HasParentheses)
			return true;
		
		var context = CurrentResolutionContext;
		if (syntax.TypePath.IsEmpty)
			return enumType.Cases.Any(enumCase => enumCase.Name == syntax.CaseName.Text) ||
			       context.Resolve(syntax.CaseName.Text) is null or TypeSymbol;
		
		var symbol = context.Resolve(syntax.TypePath[0].Text);
		foreach (var part in syntax.TypePath.Skip(1))
			symbol = symbol is ModulePathSymbol module ? context.ResolveMember(module, part.Text) : null;
		
		return symbol is null or TypeSymbol;
	}
	
	private ResolvedPattern? ResolveTestPattern(PatternNode syntax, TypeSymbol subjectType)
	{
		ProbePattern(syntax, subjectType);
		var location = syntax.SourceLocation;
		var isCopy = _typePool.IsCopy(subjectType);
		var subject = new LocalVariableSymbol(new Token(TokenType.Identifier, location, "match"),
			isCopy || IsInvalid(subjectType) ? subjectType : _typePool.GetPointerType(subjectType), false)
		{
			IsSynthesized = true,
			IsPatternBinding = true,
			IsBorrowBinding = !isCopy && !IsInvalid(subjectType)
		};
		
		var checkpoint = OpenCheckpoint();
		var resolved = ResolveExpected(NativeSymbols.Bool,
			() => ResolvePatternTest(syntax, ResolveSymbolValue(syntax.Value!, subject)));
		
		var hasErrors = checkpoint.HasErrors;
		Commit(checkpoint);
		if (IsInvalid(resolved) || hasErrors)
			return null;
		
		if (syntax.Kind != PatternKind.Range && subjectType is FloatType or UntypedFloatType &&
		    syntax.Operator?.Type is null or TokenType.OpBangEqual)
			Diagnostics.Add(new(DiagnosticSeverity.Warning, location, "Float patterns compare exactly")
			{
				Lint = Lints.FloatPattern
			});
		
		var (constant, spans) = FindPatternConstants(syntax, resolved, subjectType);
		return new ResolvedPattern(null, []) { Subject = subject, Test = resolved, Constant = constant, Spans = spans };
	}
	
	private IResolvedExpressionNode ResolvePatternTest(PatternNode syntax, IResolvedExpressionNode subject)
	{
		var value = syntax.Value!;
		if (syntax is { Kind: PatternKind.Range, Operator: { } range, Upper: { } upper })
		{
			var location = range.SourceLocation;
			ImmutableArray<Token> ops =
			[
				new(TokenType.OpLessEqual, location, "<="),
				syntax.IsInclusive ? new(TokenType.OpLessEqual, location, "<=") : new(TokenType.OpLess, location, "<")
			];
			
			var operands = VisitChainOperands([value, value, upper], [null, subject, null]);
			return ResolveChainLinks(value, ops, [value.SourceLocation, location, upper.SourceLocation], operands);
		}
		
		var op = syntax.Operator ?? new Token(TokenType.OpEqualEqual, value.SourceLocation, "==");
		return ResolveBinaryOperation(value, op, subject, VisitRightOperand(value, op, subject));
	}
	
	private (Constant? Constant, ImmutableArray<(BigInteger Low, BigInteger High)> Spans) FindPatternConstants(
		PatternNode syntax, IResolvedExpressionNode test, TypeSymbol subjectType)
	{
		if (FindLimits(subjectType) is not var (min, max))
			return (null, []);
		
		switch (test)
		{
			case ResolvedChainedExpressionNode { Operands: [var lower, _, var upper] } chain
				when chain.Links.All(static link => link.Operation is NativeImpl) &&
				     GetPatternValue(_evaluator.Evaluate(lower)) is { } low &&
				     GetPatternValue(_evaluator.Evaluate(upper)) is { } high:
				var last = syntax.IsInclusive ? high : high - 1;
				return (null, low <= last ? [(low, last)] : []);
			
			case ResolvedBinaryOpExpressionNode { Operation: NativeImpl } binary
				when _evaluator.Evaluate(binary.Right) is { } constant && GetPatternValue(constant) is { } value:
				ImmutableArray<(BigInteger, BigInteger)> spans = syntax.Operator?.Type switch
				{
					null => [(value, value)],
					TokenType.OpGreater => [(value + 1, max)],
					TokenType.OpGreaterEqual => [(value, max)],
					TokenType.OpLess => [(min, value - 1)],
					TokenType.OpLessEqual => [(min, value)],
					_ => [(min, value - 1), (value + 1, max)]
				};
				
				return (syntax.Operator is null ? constant : null,
					[..spans.Where(static span => span.Item1 <= span.Item2)]);
			
			default:
				return (null, []);
		}
	}
	
	private (BigInteger Min, BigInteger Max)? FindLimits(TypeSymbol type) => type switch
	{
		IntegerStorageType integer => _evaluator.GetLimits(integer),
		_ when type == NativeSymbols.Bool => (BigInteger.Zero, BigInteger.One),
		_ => null
	};
	
	private static BigInteger? GetPatternValue(Constant? constant) => constant switch
	{
		IntegerConstant integer => integer.Value,
		BoolConstant flag => flag.Value ? BigInteger.One : BigInteger.Zero,
		_ => null
	};
	
	private ResolvedPattern? ResolveRecordPattern(PatternNode syntax, TypeSymbol type, bool isMut, bool ownsValue)
	{
		var context = CurrentResolutionContext;
		var typeNode = CreatePatternTypeNode(syntax);
		var symbol = context.ResolveTypeName(typeNode);
		if (symbol is null)
		{
			context.ResolveType(typeNode);
			return null;
		}
		
		if (symbol != type && symbol != (type as NamedTypeSymbol)?.Definition)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, typeNode.SourceLocation, $"Expected '{type.Name}'"));
			return null;
		}
		
		return ResolveFields(syntax, type, isMut, ownsValue);
	}
	
	private static ITypeNode CreatePatternTypeNode(PatternNode pattern) => pattern.TypePath.IsEmpty
		? new IdentifierTypeNode(pattern.CaseName)
		: new QualifiedTypeNode(ResolutionContext.GetSpan([..pattern.TypePath, pattern.CaseName]),
			[..pattern.TypePath, pattern.CaseName]);
	
	private ResolvedPattern? ResolveFields(PatternNode syntax, TypeSymbol type, bool isMut, bool ownsValue)
	{
		if (type is not RecordSymbol)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, CreatePatternTypeNode(syntax).SourceLocation,
				$"Cannot destructure '{type.Name}' values"));
			
			return null;
		}
		
		var fields = new List<FieldSymbol>();
		var bindings = new List<LocalVariableSymbol?>();
		var elements = new List<ResolvedPattern?>();
		var failed = false;
		foreach (var element in syntax.Elements)
		{
			if (ResolveField(element, type) is not { } field)
			{
				failed = true;
				continue;
			}
			
			var fieldType = GetMemberType(field);
			if (!ResolveSlot(element, fieldType, isMut, false, true, out var binding, out var nested))
			{
				failed = true;
				continue;
			}
			
			if (ownsValue && binding is { IsBorrowBinding: false } && _typePool.NeedsDrop(fieldType) &&
			    type is RecordSymbol { HasDestructor: true })
				Diagnostics.Add(new(DiagnosticSeverity.Error, binding.Identifier.SourceLocation,
					"Cannot move fields out of values with destructors"));
			
			fields.Add(field);
			bindings.Add(binding);
			elements.Add(nested);
		}
		
		var names = syntax.Elements.Select(GetFieldName).OfType<Token>().ToList();
		foreach (var sameField in names.GroupBy(static name => name.Text).Where(static g => g.Count() > 1))
		{
			failed = true;
			foreach (var name in sameField)
				Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation,
					$"'{name.Text}' is named more than once"));
		}
		
		if (failed)
			return null;
		
		var resolved = new ResolvedPattern(null, bindings)
			{ Record = type, Fields = [..fields], Elements = [..elements] };
		
		ReportRepeatedBindings(resolved.AllBindings);
		return resolved;
	}
	
	private FieldSymbol? ResolveField(PatternNode element, TypeSymbol type)
	{
		if (GetFieldName(element) is not { Text: not "_" } name)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, element.SourceLocation, "Expected a field name"));
			return null;
		}
		
		if (_typePool.ResolveMember(type, name.Text) is not FieldSymbol field)
		{
			Diagnostics.Add(DescribeAbsentMember(type, name.Text).At(name.SourceLocation));
			return null;
		}
		
		if (CanAccess(type, field.Visibility))
			return field;
		
		Diagnostics.Add(DiagnosticReporter.ReportHidden(name.SourceLocation, name.Text, field.Visibility, true));
		return null;
	}
	
	private ResolvedPattern? ResolveTypePattern(PatternNode pattern, DynType dyn, bool isMut, PatternNode? trailing)
	{
		if (pattern.HasParentheses && !pattern.IsDestructuring)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, pattern.SourceLocation, "Expected a type"));
			return null;
		}
		
		var context = CurrentResolutionContext;
		var typeNode = pattern.Type ?? (pattern.TypePath.IsEmpty
			? new IdentifierTypeNode(pattern.CaseName)
			: new QualifiedTypeNode(pattern.SourceLocation, [..pattern.TypePath, pattern.CaseName]));
		
		var type = context.ResolveType(typeNode);
		if (IsInvalid(type))
			return null;
		
		if (type is DynType)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, typeNode.SourceLocation, $"Cannot test for '{type.Name}'"));
			return null;
		}
		
		if (!_typePool.Conforms(type, dyn))
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, typeNode.SourceLocation,
				$"'{type.Name}' doesn't implement '{dyn.TraitName}'"));
			
			return null;
		}
		
		if (trailing?.Mode is { } mode)
			Diagnostics.Add(
				new(DiagnosticSeverity.Error, mode.SourceLocation, $"Cannot use '{mode.Text}' after a type"));
		
		if (trailing?.CaseName is { } named)
			RejectBuiltInName(named);
		
		var binding = trailing?.CaseName is { Text: not "_" } token
			? new LocalVariableSymbol(token, _typePool.GetPointerType(type), false)
			{
				IsPatternBinding = true,
				IsBorrowBinding = true,
				IsMutBinding = isMut
			}
			: null;
		
		if (!pattern.IsDestructuring)
			return new ResolvedPattern(null, []) { TestedType = type, TypeBinding = binding };
		
		return ResolveFields(pattern, type, isMut, false) is { } fields
			? new ResolvedPattern(null, fields.Bindings)
			{
				TestedType = type,
				TypeBinding = binding,
				Record = type,
				Fields = fields.Fields,
				Elements = fields.Elements
			}
			: null;
	}
	
	private LocalVariableSymbol? CreateBinding(Token token, Token? mode, TypeSymbol type, bool isMut, bool ownsValue)
	{
		if (token.Text == "_")
		{
			if (mode is { } discarded)
				Diagnostics.Add(DiagnosticFix.Redundant(discarded.SourceLocation));
			
			return null;
		}
		
		var modeType = mode?.Type;
		var isCopy = _typePool.IsCopy(type);
		var copies = modeType == TokenType.KeywordVar && isCopy;
		if (modeType == TokenType.KeywordMut && ownsValue)
			Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation,
				"Cannot mutably borrow unstored values"));
		else if (modeType is TokenType.KeywordOwn or TokenType.KeywordVar && isMut && !copies)
			Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation,
				"Cannot move mutably borrowed payloads"));
		else if (modeType == TokenType.KeywordOwn && !ownsValue && isCopy)
			Diagnostics.Add(DiagnosticFix.Redundant(mode!.Value.SourceLocation));
		
		var isMutBinding = !ownsValue && !copies && (isMut || modeType == TokenType.KeywordMut);
		var isBorrowBinding = !ownsValue && !copies && (isMutBinding || !isCopy);
		var bindingType = isBorrowBinding && type is not InvalidType ? _typePool.GetPointerType(type) : type;
		RejectBuiltInName(token);
		return new LocalVariableSymbol(token, bindingType, modeType == TokenType.KeywordVar && !isBorrowBinding)
		{
			IsPatternBinding = true,
			IsBorrowBinding = isBorrowBinding,
			IsMutBinding = isMutBinding
		};
	}
	
	private bool IsEnumName(ImmutableArray<Token> typePath, EnumSymbol enumType)
	{
		var context = CurrentResolutionContext;
		var symbol = typePath is [var name] ? context.Resolve(name.Text) : context.ResolveQualifiedName(typePath);
		switch (symbol)
		{
			case not null when symbol == enumType || symbol == enumType.Definition:
				return true;
			
			case null when typePath is [var typeName]:
				var typeNames = context.GetAllSymbols()
					.OfType<TypeSymbol>()
					.Select(static t => t.Name)
					.Distinct();
				
				Diagnostics.Add(context.ReportHidden(typeName.Text, typeName.SourceLocation) ??
				                DiagnosticReporter.ReportUndefinedType(typeName.SourceLocation, typeName.Text,
					                typeNames));
				
				return false;
			
			case null:
				return false;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, ResolutionContext.GetSpan(typePath),
					$"Expected a case of '{enumType.Name}'"));
				
				return false;
		}
	}
	
	private void ReportRepeatedBindings(IEnumerable<LocalVariableSymbol> bindings)
	{
		foreach (var sameName in bindings.GroupBy(static b => b.Name).Where(static g => g.Count() > 1))
		{
			foreach (var binding in sameName)
			{
				if (!_repeatedBindings.Add(binding))
					continue;
				
				Journal(() => _repeatedBindings.Remove(binding));
				Diagnostics.Add(new(DiagnosticSeverity.Error, binding.Identifier.SourceLocation,
					$"'{binding.Name}' is bound more than once"));
			}
		}
	}
	
	private IEnumerable<LocalVariableSymbol> GetTrueBindings(IResolvedExpressionNode condition) =>
		condition switch
		{
			ResolvedIsExpressionNode node => node.Pattern.AllBindings,
			ResolvedInvalidExpressionNode node when _failedPatterns.TryGetValue(node, out var bindings) =>
				bindings.OfType<LocalVariableSymbol>(),
			ResolvedBinaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpAmpersandAmpersand } } node =>
				GetTrueBindings(node.Left).Concat(GetTrueBindings(node.Right)),
			_ => []
		};
	
	private void ReportDeclarationBody(IStatementNode body)
	{
		if (body is VarStatementNode declaration)
			Diagnostics.Add(new(DiagnosticSeverity.Error, declaration.SourceLocation,
				"Cannot use declarations as whole bodies"));
	}
	
	private Scope CreateScope(IEnumerable<LocalVariableSymbol?> bindings)
	{
		var scope = CurrentScope?.CreateChild() ?? new Scope();
		foreach (var binding in bindings.OfType<LocalVariableSymbol>())
			scope.Define(binding);
		
		return scope;
	}
	
	private IResolvedStatementNode VisitInScope(IStatementNode node, IEnumerable<LocalVariableSymbol?> bindings)
	{
		_resolutionContexts.Push(CurrentResolutionContext with { LocalScope = CreateScope(bindings) });
		var result = VisitNode(node);
		_resolutionContexts.Pop();
		return result;
	}
	
	private IResolvedExpressionNode VisitInScope(IExpressionNode node, IEnumerable<LocalVariableSymbol?> bindings,
		TypeSymbol? target = null, TypeSymbol? hint = null)
	{
		_resolutionContexts.Push(CurrentResolutionContext with { LocalScope = CreateScope(bindings) });
		var result = target is null && hint is not null ? VisitHinted(node, hint) : VisitNode(node, target);
		_resolutionContexts.Pop();
		return result;
	}
}