using System.Numerics;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private sealed record ForHeader
	(
		LocalVariableSymbol? Binding,
		Func<IResolvedStatementNode, LabelSymbol?, IResolvedStatementNode> Build
	);
	
	private ForHeader ResolveRange(ForStatementNode node, IExpressionNode endNode)
	{
		List<IResolvedExpressionNode> bounds = [Decay(VisitNode(node.Source)), Decay(VisitNode(endNode))];
		if (node.Mode is { } mode)
			Diagnostics.Add(new(DiagnosticSeverity.Error, mode.SourceLocation,
				"Cannot iterate over ranges with 'mut'"));
		
		var open = settledTypes is null && survey is not null && bounds.All(IsOpenValue)
			? DeclareOpen(new(node, node.Binding, node.Source, CurrentResolutionContext) { IsCounter = true }, bounds)
			: null;
		
		var type = settledTypes?.GetValueOrDefault(node) ??
		           (AnyInvalid(bounds[0], bounds[1]) ? NativeSymbols.Invalid : FindBoundType(bounds));
		
		if (type is not (InvalidType or IntegerType))
		{
			bounds = [..bounds.Select(MaterializeAsDefault)];
			var (source, range) = node.Source.SourceLocation;
			Diagnostics.Add(new(DiagnosticSeverity.Error, new(source, range.Join(endNode.SourceLocation.Range)),
				type is null
					? $"Cannot combine '{bounds[0].Type.Name}' and '{bounds[1].Type.Name}' range bounds"
					: $"Cannot use '{type.Name}' as a range bound"));
			
			type = NativeSymbols.Invalid;
		}
		
		var binding = CreateLoopBinding(node.Binding, type, false, false);
		if (open is not null && binding is not null)
			survey!.Bind(binding, open);
		
		if (type is InvalidType)
			return InvalidHeader(node, binding);
		
		var counter = binding ?? new LocalVariableSymbol(HiddenName(node.Binding, "counter"), type, false);
		var start = CoerceToType(bounds[0], type);
		var end = CoerceToType(bounds[1], type);
		var isInclusive = node.RangeOperator?.Type == TokenType.OpDotDotEqual;
		return new(binding, (body, label) =>
			new ResolvedRangeForStatementNode(counter, start, end, isInclusive, null, null, body, label, node));
	}
	
	private TypeSymbol? FindBoundType(List<IResolvedExpressionNode> bounds)
	{
		if (bounds.All(static bound => bound.Type is UntypedType))
			return UnifyTypes(bounds);
		
		if (bounds.All(static bound => bound.Type == NativeSymbols.Char))
			return NativeSymbols.Char;
		
		var trial = OpenCheckpoint();
		var sums = ResolveCallable(_operatorRegistry.GetBinaryCandidates(TokenType.OpPlus), [..bounds],
			MaterializationMode.Overload);
		
		Rollback(trial);
		return sums is { HasResult: true, IsAmbiguous: false } &&
		       sums[0].Callable.ParameterTypes is [var left, var right] && left == right
			? left
			: null;
	}
	
	private ForHeader ResolveIteration(ForStatementNode node)
	{
		var source = VisitNode(node.Source);
		if (!IsInvalid(source) && source.Type is UntypedType)
			source = MaterializeAsDefault(source);
		
		source = Decay(source);
		if (IsInvalid(source))
			return InvalidHeader(node, CreateLoopBinding(node.Binding, NativeSymbols.Invalid, false, false));
		
		if (source.Type is NeverType)
			return new(CreateLoopBinding(node.Binding, NativeSymbols.Never, false, false),
				(_, _) => new ResolvedExpressionStatementNode(source, node));
		
		var isMut = node.Mode is not null;
		var type = source.Type;
		if (type is ArrayType array)
			return ResolveArrayIteration(node, source, array, isMut);
		
		if (FindLoopOperators(type, "in") is [_, ..] collection)
			return ResolveCollection(node, source, collection, isMut);
		
		if (FindLoopOperators(type, "for") is not [_, ..])
			return RejectIteration(node, $"Cannot iterate over '{type.Name}'", node.Source.SourceLocation);
		
		if (isMut)
		{
			var pointer = _typePool.GetPointerType(type);
			var cursor = new LocalVariableSymbol(HiddenName(node.Binding, "cursor"), pointer, false)
			{
				IsBorrowBinding = true,
				IsMutBinding = true,
				IsLoopSource = true
			};
			
			return ResolveCursor(node, cursor,
				new ResolvedMutArgumentExpressionNode(MakeWritable(source), pointer, node.Source), true);
		}
		
		return TypePool.FindValueDyn(type) is { } dyn
			? RejectIteration(node, $"Cannot use '{dyn.Name}' by value", node.Source.SourceLocation)
			: ResolveCursor(node,
				new LocalVariableSymbol(HiddenName(node.Binding, "cursor"), type, true) { IsLoopSource = true }, source,
				false);
	}
	
	private IResolvedExpressionNode? ResolveArrayBuild(CallExpressionNode node, ArrayType array, IExpressionNode maker)
	{
		var function = _typePool.GetFunctionType(false, [NativeSymbols.UIntSize], [ParameterMode.ReadOnly],
			array.ElementType, true);
		
		IResolvedExpressionNode make;
		if (maker is LambdaExpressionNode lambda)
			make = CoerceToType(ResolveLambda(lambda, function, true, true), function);
		else
		{
			var trial = OpenCheckpoint();
			var value = VisitNode(maker, null);
			if (value.Type is not (FunctionType or FunctionGroupType))
			{
				Rollback(trial);
				return null;
			}
			
			Commit(trial);
			make = CoerceToType(value, function);
		}
		
		if (IsInvalid(make))
			return new ResolvedInvalidExpressionNode(node, array);
		
		var length = array.LengthParameter is { } parameter
			? ConvertLength(new ResolvedValueParameterExpressionNode(parameter, node))
			: MaterializeLiteral(node, NativeSymbols.UIntSize, array.Length);
		
		return new ResolvedArrayBuildExpressionNode(array, make, length, function, node);
	}
	
	private ForHeader ResolveArrayIteration(ForStatementNode node, IResolvedExpressionNode source, ArrayType array,
		bool isMut)
	{
		var syntax = node.Source;
		var pointer = _typePool.GetPointerType(array);
		var sourceSymbol = new LocalVariableSymbol(HiddenName(node.Binding, "source"), pointer, false)
		{
			IsBorrowBinding = true,
			IsMutBinding = isMut,
			IsLoopSource = true
		};
		
		IResolvedExpressionNode sourceValue = isMut
			? new ResolvedMutArgumentExpressionNode(MakeWritable(source), pointer, syntax)
			: source;
		
		var index = new LocalVariableSymbol(HiddenName(node.Binding, "index"), NativeSymbols.UIntSize, false);
		var start = MaterializeLiteral(syntax, NativeSymbols.UIntSize, BigInteger.Zero);
		var end = array.LengthParameter is { } length
			? ConvertLength(new ResolvedValueParameterExpressionNode(length, syntax))
			: MaterializeLiteral(syntax, NativeSymbols.UIntSize, array.Length);
		
		var elementType = array.ElementType;
		var binding = CreateLoopBinding(node.Binding, elementType, isMut || !_typePool.IsCopy(elementType), isMut);
		if (!isMut && binding is not null && FindElement(source) is { } open)
			survey!.Bind(binding, open);
		
		var element = binding is null
			? null
			: new ResolvedLoopVariable(binding, new ResolvedIndexerExpressionNode(elementType,
				Dereference(sourceSymbol, syntax), new ResolvedVarExpressionNode(index, index.Type, syntax), syntax));
		
		return new(binding, (body, label) => new ResolvedRangeForStatementNode(index, start, end, false,
			new(sourceSymbol, sourceValue), element, body, label, node));
	}
	
	private IResolvedExpressionNode ConvertLength(IResolvedExpressionNode length) =>
		length.Type == NativeSymbols.UIntSize || IsInvalid(length)
			? length
			: new ResolvedConversionExpressionNode(length,
				_conversionTable.FindExplicit(length.Type, NativeSymbols.UIntSize)!, length.Syntax);
	
	private ForHeader ResolveCollection(ForStatementNode node, IResolvedExpressionNode source,
		List<MethodSymbol> operators, bool isMut)
	{
		var type = source.Type;
		var mode = isMut ? ParameterMode.Mut : ParameterMode.ReadOnly;
		var location = node.Source.SourceLocation;
		var sameMode = operators.Where(method => TakesReceiver(method, type, mode)).ToList();
		if (isMut && sameMode.Count == 0)
			sameMode = operators.Where(method => TakesReceiver(method, type, ParameterMode.ReadOnly) &&
			                                     LendsWritably(GetFunctionInfo(method, type).Signature.ReturnType))
				.ToList();
		
		if (sameMode.FirstOrDefault(method => CanAccess(type, method.Function)) is not { } method)
			return sameMode.Count > 0
				? RejectIteration(node, ReportHiddenMember(location, "in", sameMode.Select(static m => m.Function)))
				: RejectIteration(node,
					$"Cannot iterate over '{type.Name}' {(isMut ? "with" : "without")} 'mut'", location);
		
		if (sameMode.Count(other => CanAccess(type, other.Function)) > 1)
			return RejectIteration(node, DescribeAmbiguousOperator("in", type), location);
		
		var call = CallOperator(source, method, node.Source);
		return ResolveCursor(node, new LocalVariableSymbol(HiddenName(node.Binding, "cursor"), call.Type, true), call,
			isMut);
	}
	
	private ForHeader ResolveCursor(ForStatementNode node, LocalVariableSymbol cursor,
		IResolvedExpressionNode cursorValue, bool isMut)
	{
		var syntax = node.Source;
		var type = cursor.Type is PointerType { BaseType: var target } && cursor.IsBorrowBinding ? target : cursor.Type;
		var steps = FindLoopOperators(type, "for");
		if (steps.FirstOrDefault(method => CanAccess(type, method.Function)) is not { } step)
			return steps.Count > 0
				? RejectIteration(node, ReportHiddenMember(syntax.SourceLocation, "for",
					steps.Select(static method => method.Function)))
				: RejectIteration(node, $"'{type.Name}' has no 'for' operator", syntax.SourceLocation);
		
		if (steps.Count(other => CanAccess(type, other.Function)) > 1)
			return RejectIteration(node, DescribeAmbiguousOperator("for", type), syntax.SourceLocation);
		
		var stepCall = CallOperator(ReadCursor(), step, syntax);
		if (node.Binding.Text == "_")
			return new(null, (body, label) =>
				new ResolvedCursorForStatementNode(new(cursor, cursorValue), stepCall, null, body, label, node));
		
		var dereference = isMut
			? FindDereference(type, ParameterMode.Mut) ?? FindDereference(type, ParameterMode.ReadOnly)
			: FindDereference(type, ParameterMode.ReadOnly) ?? FindDereference(type, ParameterMode.Mut);
		
		if (dereference is null)
		{
			var location = node.Binding.SourceLocation;
			var hidden = FindLoopOperators(type, "*");
			return hidden.Count > 0
				? RejectIteration(node, ReportHiddenMember(location, "*", hidden.Select(static m => m.Function)))
				: RejectIteration(node, $"'{type.Name}' has no '*' operator", location);
		}
		
		if (IsAmbiguousOperator(type, dereference))
			return RejectIteration(node, DescribeAmbiguousOperator("*", type), node.Binding.SourceLocation);
		
		var element = Decay(CallOperator(ReadCursor(), dereference, syntax));
		var isWritable = ReturnsWritable(dereference, type) &&
		                 (isMut || TakesReceiver(dereference, type, ParameterMode.ReadOnly));
		
		var lends = GetFunctionInfo(dereference, type).Signature.ReturnType is BorrowType;
		var binding = CreateLoopBinding(node.Binding, element.Type,
			isWritable || lends && !_typePool.IsCopy(element.Type), isWritable)!;
		
		return new(binding, (body, label) => new ResolvedCursorForStatementNode(new(cursor, cursorValue), stepCall,
			new(binding, element), body, label, node));
		
		IResolvedExpressionNode ReadCursor() => cursor.IsBorrowBinding
			? Dereference(cursor, syntax)
			: new ResolvedVarExpressionNode(cursor, cursor.Type, syntax);
	}
	
	private List<MethodSymbol> FindLoopOperators(TypeSymbol type, string name) =>
		[..GetMethods(type, name).Where(static method => method.HasReceiver)];
	
	private bool LendsWritably(TypeSymbol cursor) =>
		FindDereferences(cursor).Any(dereference => ReturnsWritable(dereference, cursor));
	
	private bool ReturnsWritable(MethodSymbol method, TypeSymbol type) =>
		GetFunctionInfo(method, type).Signature.ReturnType is BorrowType { IsMutable: true };
	
	private ForHeader RejectIteration(ForStatementNode node, string message, SourceLocation location) =>
		RejectIteration(node, new Diagnostic(DiagnosticSeverity.Error, location, message));
	
	private ForHeader RejectIteration(ForStatementNode node, Diagnostic diagnostic)
	{
		Diagnostics.Add(diagnostic);
		return InvalidHeader(node, CreateLoopBinding(node.Binding, NativeSymbols.Invalid, false, false));
	}
	
	private static ForHeader InvalidHeader(ForStatementNode node, LocalVariableSymbol? binding) =>
		new(binding, (_, _) => new ResolvedInvalidStatementNode(node));
	
	private LocalVariableSymbol? CreateLoopBinding(Token token, TypeSymbol type, bool isBorrow, bool isMut) =>
		token.Text == "_"
			? null
			: new(token, isBorrow && type is not InvalidType ? _typePool.GetPointerType(type) : type, false)
			{
				IsLoopBinding = true,
				IsBorrowBinding = isBorrow,
				IsMutBinding = isMut
			};
	
	private static Token HiddenName(Token binding, string name) =>
		new(TokenType.Identifier, binding.SourceLocation, $".{name}");
	
	private static ResolvedUnaryOpExpressionNode Dereference(LocalVariableSymbol pointer, IExpressionNode syntax) =>
		new(new ResolvedVarExpressionNode(pointer, pointer.Type, syntax),
			new NativeImpl(TokenType.OpStar, ((PointerType)pointer.Type).BaseType), syntax);
}