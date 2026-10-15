using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	public IResolvedExpressionNode Visit(UnaryOpExpressionNode node)
	{
		var op = node.Op;
		var isLiteral = node.Operand is LiteralExpressionNode;
		if (isLiteral)
			_unaryOpJobs.Push(new(op.Type));
		
		var outerTarget = placeTarget;
		if (op.Type == TokenType.OpAt)
			placeTarget = node.Operand;
		
		var operand = VisitNode(node.Operand, null);
		placeTarget = outerTarget;
		var consumed = isLiteral && _unaryOpJobs.Pop().Consumed;
		
		if (consumed)
			return operand is ResolvedLiteralExpressionNode literal
				? new ResolvedLiteralExpressionNode(literal.Type, literal.Value, node)
				: operand;
		
		if (IsInvalid(operand))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (operand.Type is NeverType && op.Type != TokenType.OpAt)
			return operand;
		
		operand = Decay(operand);
		switch (op.Type)
		{
			// Special unary operators that aren't stored in the registry
			case TokenType.OpAt:
				return ResolveAddressOf(op.Type, operand, node);
			
			case TokenType.OpStar:
				return ResolveDereference(op.Type, operand, node);
			
			default:
			{
				if (FoldLiteral(node, operand) is { } folded)
					return folded;
				
				if (ResolveDeclaredUnary(node, operand) is { } declared)
					return declared;
				
				if (op.Type == TokenType.OpMinus && operand.Type is IntegerType { IsSigned: false })
				{
					var negation = new Diagnostic(DiagnosticSeverity.Error, node.SourceLocation,
						$"Operator '-' cannot be applied to '{operand.Type.Name}'")
					{
						Hints = [$"'{operand.Type.Name}' can't represent negative values"]
					};
					
					return Error(node, negation, CurrentTargetType);
				}
				
				var candidates = _operatorRegistry.GetUnaryCandidates(op.Type);
				var operandArray = new[] { operand };
				var resolutionSet = ResolveCallable(candidates, operandArray, MaterializationMode.Overload);
				
				if (resolutionSet.IsAmbiguous)
					return Error(node, $"Operation '{op.Text}' on '{operand.Type.Name}' is ambiguous",
						CurrentTargetType);
				
				if (!resolutionSet.HasResult)
					return Error(node, $"Operator '{op.Text}' cannot be applied to '{operand.Type.Name}'",
						CurrentTargetType);
				
				var resolution = resolutionSet[0];
				var resolvedOperand = ApplyArgumentResolution(operandArray, resolution);
				var operation = (OperationImpl)resolution.Callable;
				var result = new ResolvedUnaryOpExpressionNode(resolvedOperand[0], operation, node);
				
				return ApplyResultResolution(result, resolution);
			}
		}
	}
	
	private IResolvedExpressionNode ResolveDereference(TokenType opType, IResolvedExpressionNode operand,
		IExpressionNode node) => operand.Type switch
	{
		PointerType { BaseType: { } baseType } => baseType == NativeSymbols.Void
			? Error(node, "Cannot dereference untyped pointers", CurrentTargetType)
			: new ResolvedUnaryOpExpressionNode(operand, new NativeImpl(opType, baseType), node),
		var type when (FindDereference(type, ParameterMode.ReadOnly) ?? FindDereference(type, ParameterMode.Mut)) is
			{ } dereference => IsAmbiguousOperator(type, dereference)
				? Error(node, DescribeAmbiguousOperator("*", type), CurrentTargetType)
				: Decay(CallOperator(operand, dereference, node)),
		var type when GetMethods(type, "*").Where(static method => method.HasReceiver).ToList() is
			{ Count: > 0 } hidden => Error(node, ReportHiddenMember(node.SourceLocation, "*",
			hidden.Select(static method => method.Function)), CurrentTargetType),
		_ => Error(node, $"Cannot dereference type '{operand.Type.Name}'", CurrentTargetType)
	};
	
	private ResolvedUnaryOpExpressionNode ResolveAddressOf(TokenType opType, IResolvedExpressionNode operand,
		UnaryOpExpressionNode node)
	{
		var ptrType = _typePool.GetPointerType(operand.Type);
		return new(operand, new NativeImpl(opType, ptrType), node);
	}
	
	private static bool IsAssignment(TokenType op) => op is TokenType.OpEqual or TokenType.OpPlusEqual
		or TokenType.OpMinusEqual or TokenType.OpStarEqual or TokenType.OpSlashEqual or TokenType.OpPercentEqual
		or TokenType.OpPlusPercentEqual or TokenType.OpMinusPercentEqual or TokenType.OpStarPercentEqual
		or TokenType.OpAmpersandEqual or TokenType.OpBarEqual or TokenType.OpHatEqual or TokenType.OpLessLessEqual
		or TokenType.OpGreaterGreaterEqual or TokenType.OpLessLessLessEqual or TokenType.OpGreaterGreaterGreaterEqual;
	
	public IResolvedExpressionNode Visit(BinaryOpExpressionNode node)
	{
		var op = node.Op;
		if (IsAssignment(op.Type))
		{
			var isOwnStore = node.Left is OwnExpressionNode;
			var outerTarget = storeTarget;
			storeTarget = node.Left is OwnExpressionNode target ? target.Value : node.Left;
			var left = VisitNode(storeTarget, null);
			storeTarget = outerTarget;
			if (FindStoredLocals(left) is [_, ..] stored)
				return RecordStore(node, stored);
			
			if (left is ResolvedPropertyExpressionNode property)
				return ResolvePropertyAssignment(node, property, isOwnStore);
			
			if (left is ResolvedFunctionGroupExpressionNode)
			{
				VisitNode(node.Right, null);
				return Error(node, "Cannot reassign functions", CurrentTargetType, node.Left);
			}
			
			left = MakeWritable(left);
			if (left.Type is DynType dyn)
			{
				VisitNode(node.Right, null);
				return Error(node, $"Cannot use '{dyn.Name}' by value", CurrentTargetType, node.Left);
			}
			
			if (op.Type != TokenType.OpEqual)
				return ResolveCompoundAssignment(node, Decay(MaterializeAsDefault(left)), isOwnStore);
			
			if (left.Type is BorrowType borrow)
				return ResolveBorrowAssignment(node, left, borrow, isOwnStore);
			
			var right = VisitNode(node.Right, left.Type);
			return new ResolvedAssignmentExpressionNode(left.Type, left, op, right, null, node, isOwnStore);
		}
		else
		{
			var candidates = _operatorRegistry.GetBinaryCandidates(op.Type);
			var operators = candidates;
			var isConjunction = op.Type == TokenType.OpAmpersandAmpersand;
			IResolvedExpressionNode left, right;
			var leftTrial = OpenCheckpoint();
			left = Decay(VisitOperand(node.Left, candidates, 0));
			if (!isConjunction && FailsInference(node.Left, left, leftTrial))
			{
				Rollback(leftTrial);
				right = Decay(VisitOperand(node.Right, candidates, 1));
				left = Decay(VisitPeerOperand(node.Left, () => VisitOperand(node.Left, operators, 0), right.Type));
			}
			else
			{
				Commit(leftTrial);
				var leftType = left.Type;
				right = Decay(isConjunction
					? VisitInScope(node.Right, GetTrueBindings(left))
					: VisitPeerOperand(node.Right,
						() => VisitOperand(node.Right, [..operators, ..FindOperatorCallables(leftType, op.Text)], 1,
							leftType),
						leftType));
			}
			
			if (isConjunction)
				ReportRepeatedBindings(GetTrueBindings(left).Concat(GetTrueBindings(right)));
			
			if (AnyInvalid(left, right))
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			
			if (left.Type is NeverType)
				return left;
			
			if (right.Type is NeverType && left.Type is not UntypedType)
				right = MaterializeExpression(right, left.Type);
			
			(left, right) = (AdaptCharLiteral(left, right.Type), AdaptCharLiteral(right, left.Type));
			if (AnyInvalid(left, right))
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			
			if (FoldLiterals(node, left, right) is { } folded)
				return folded;
			
			if (survey is { } activeSurvey && left.Type is UntypedType && right.Type is UntypedType)
			{
				activeSurvey.MarkDefaulted(left);
				activeSurvey.MarkDefaulted(right);
			}
			
			if (ResolveDeclaredOperator(node, left, right) is { } declared)
				return declared;
			
			if (op.Type == TokenType.OpPlus && IsString(left.Type) && IsString(right.Type))
				return ConcatenateStrings(node, left, right);
			
			if (IsMixedSignComparison(op.Type, left.Type, right.Type))
			{
				var comparison = new NativeImpl(op.Type, NativeSymbols.Bool, left.Type, right.Type);
				return new ResolvedBinaryOpExpressionNode(left, right, comparison, node);
			}
			
			if (!IsShiftOrRotate(op.Type) &&
			    FindLossyMixedSign(left.Type, right.Type) is var (signedType, unsignedType))
			{
				if (FindMixedSignType(signedType, unsignedType) is not { } mixedType)
				{
					var hint = $"'{signedType.Name}' can't represent every '{unsignedType.Name}' value";
					if (CountBits(signedType) > CountBits(unsignedType))
						hint += signedType == NativeSymbols.IntSize ? " on 32-bit targets" : " on 64-bit targets";
					
					var mismatch = DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, op, right);
					return Error(node, mismatch with { Hints = [hint] }, CurrentTargetType);
				}
				
				candidates = [..candidates.Where(candidate => candidate.ParameterTypes.All(type => type == mixedType))];
			}
			
			if (op.Type is TokenType.OpEqualEqual or TokenType.OpBangEqual &&
			    FindNullableOperand(left, right) is { } nullable)
				return new ResolvedBinaryOpExpressionNode(MaterializeExpression(left, nullable),
					MaterializeExpression(right, nullable),
					new NativeImpl(op.Type, NativeSymbols.Bool, nullable, nullable),
					node);
			
			if (left.Type is PointerType { BaseType: var leftBase } &&
			    right.Type is PointerType { BaseType: var rightBase } &&
			    leftBase != rightBase && leftBase != NativeSymbols.Void && rightBase != NativeSymbols.Void)
				return Error(node, DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, op, right),
					CurrentTargetType);
			
			var args = new[] { left, right };
			var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
			if (resolutionSet.IsAmbiguous &&
			    (IsShift(op.Type) && (left.Type == NativeSymbols.Char || right.Type == NativeSymbols.Char) ||
			     MixesCharWithInteger(left.Type, right.Type) ||
			     left.Type == NativeSymbols.CStr || right.Type == NativeSymbols.CStr))
				return Error(node, DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, op, right),
					CurrentTargetType);
			
			if (resolutionSet.IsAmbiguous)
				return Error(node,
					$"Operation '{op.Text}' between '{left.Type.Name}' and '{right.Type.Name}' is ambiguous",
					CurrentTargetType);
			
			if (!resolutionSet.HasResult)
			{
				if (left.Type is UntypedType || right.Type is UntypedType)
				{
					var defaultLeft = DefaultLiteral(left);
					var defaultRight = DefaultLiteral(right);
					if (AnyInvalid(defaultLeft, defaultRight))
						return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
					
					if (ResolveDeclaredOperator(node, defaultLeft, defaultRight) is { } defaulted)
						return defaulted;
				}
				
				left = ResolveUnmatched(left, right.Type);
				right = ResolveUnmatched(right, left.Type);
				if (AnyInvalid(left, right))
					return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
				
				var diagnostic = DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, op, right);
				return Error(node, diagnostic, CurrentTargetType);
			}
			
			var resolution = resolutionSet[0];
			var resolvedArgs = ApplyArgumentResolution(args, resolution);
			var operation = (OperationImpl)resolution.Callable;
			
			if (FindShiftRangeError(node, resolvedArgs[0].Type, resolvedArgs[1]) is { } rangeError)
				return Error(node, rangeError, CurrentTargetType);
			
			var result = new ResolvedBinaryOpExpressionNode(resolvedArgs[0], resolvedArgs[1], operation, node);
			return ApplyResultResolution(result, resolution);
		}
	}
	
	private IEnumerable<(TypeSymbol Owner, MethodSymbol Method)> FindOperators(TypeSymbol type, string name) =>
		GetMethods(type, name)
			.Where(static method => !method.HasReceiver)
			.Select(method => (type, method));
	
	private IEnumerable<ICallable> FindOperatorCallables(TypeSymbol type, string name) => FindOperators(type, name)
		.Where(entry => CanAccess(entry.Owner, entry.Method.Function))
		.Select(entry => new FunctionCallable(GetFunctionInfo(entry.Method, entry.Owner)));
	
	private IResolvedExpressionNode? ResolveDeclaredOperator(BinaryOpExpressionNode node, IResolvedExpressionNode left,
		IResolvedExpressionNode right)
	{
		var name = node.Op.Text;
		var declared = FindOperators(left.Type, name).ToList();
		if (right.Type != left.Type)
			declared.AddRange(FindOperators(right.Type, name));
		
		if (declared.Count == 0)
			return null;
		
		var accessible = declared
			.Where(entry => CanAccess(entry.Owner, entry.Method.Function))
			.Select(entry => new FunctionCallable(GetFunctionInfo(entry.Method, entry.Owner)))
			.ToArray();
		
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(node.Op.SourceLocation, name,
				declared.Select(static entry => entry.Method.Function)), CurrentTargetType);
		
		var args = new[] { left, right };
		var violations = new List<Diagnostic>();
		ICallable[] candidates =
		[
			..accessible
				.Select(candidate => Specialize(candidate, name, args, null, node.Op.SourceLocation, [], violations))
				.OfType<ICallable>()
		];
		
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Operation '{name}' between '{left.Type.Name}' and '{right.Type.Name}' is ambiguous",
				CurrentTargetType);
		
		if (!resolutionSet.HasResult && violations is [var violation])
			return Error(node, violation, CurrentTargetType);
		
		if (!resolutionSet.HasResult)
			return null;
		
		var resolution = resolutionSet[0];
		var info = ((FunctionCallable)resolution.Callable).Info;
		TrackFunctionUse(info, node);
		return new ResolvedFunctionCallExpressionNode(info, ApplyArgumentResolution(args, resolution), node);
	}
	
	private IResolvedExpressionNode? ResolveDeclaredUnary(UnaryOpExpressionNode node, IResolvedExpressionNode operand)
	{
		var name = node.Op.Text;
		var declared = GetMethods(operand.Type, name).Where(static method => method.HasReceiver).ToList();
		if (declared.Count == 0)
			return null;
		
		if (declared.FirstOrDefault(method => CanAccess(operand.Type, method.Function)) is not { } unary)
			return Error(node, ReportHiddenMember(node.Op.SourceLocation, name,
				declared.Select(static method => method.Function)), CurrentTargetType);
		
		var info = GetFunctionInfo(unary, operand.Type);
		TrackFunctionUse(info, node);
		return new ResolvedFunctionCallExpressionNode(info, [CreateReceiver(operand, info)], node);
	}
	
	private IResolvedExpressionNode? ResolveDeclaredCompound(BinaryOpExpressionNode node, IResolvedExpressionNode left)
	{
		var name = node.Op.Text;
		var declared = GetMethods(left.Type, name).Where(static method => method.HasReceiver).ToList();
		if (declared.Count == 0)
			return null;
		
		ICallable[] candidates =
		[
			..declared
				.Where(method => CanAccess(left.Type, method.Function))
				.Select(method => GetFunctionInfo(method, left.Type))
				.Select(static info => new ReceiverCallable(info, info.Signature.ReturnType))
		];
		
		if (candidates.Length == 0)
			return RejectAssignment(node, ReportHiddenMember(node.Op.SourceLocation, name,
				declared.Select(static method => method.Function)));
		
		var right = VisitArgument(node.Right, ParameterTypesAt(candidates, 0));
		if (IsInvalid(right))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		candidates =
		[
			..candidates
				.Select(candidate => Specialize(candidate, name, [right], null, node.Op.SourceLocation, []))
				.OfType<ICallable>()
		];
		
		var resolutionSet = ResolveCallable(candidates, [right], MaterializationMode.Overload);
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Operation '{name}' between '{left.Type.Name}' and '{right.Type.Name}' is ambiguous",
				CurrentTargetType);
		
		if (!resolutionSet.HasResult)
			return Error(node, DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, node.Op, right),
				CurrentTargetType);
		
		var resolution = resolutionSet[0];
		var info = ((ReceiverCallable)resolution.Callable).Info;
		TrackFunctionUse(info, node);
		var args = ApplyArgumentResolution([right], resolution);
		args.Insert(0, CreateReceiver(left, info));
		return new ResolvedFunctionCallExpressionNode(info, args, node);
	}
	
	public IResolvedExpressionNode Visit(ChainedExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		var visited = node.Operands
			.Select(operand => IsCaseNameSyntax(operand) ? null : Decay(VisitNode(operand, null)))
			.ToArray();
		
		var peers = visited.OfType<IResolvedExpressionNode>().Select(static operand => operand.Type).ToList();
		var operands = node.Operands
			.Select((operand, i) => visited[i] ?? VisitCaseName(operand, peers) ?? Decay(VisitNode(operand, null)))
			.ToList();
		
		for (var i = 0; i < operands.Count; i++)
		{
			if (i > 0)
				operands[i] = AdaptCharLiteral(operands[i], operands[i - 1].Type);
			
			if (i + 1 < operands.Count)
				operands[i] = AdaptCharLiteral(operands[i], operands[i + 1].Type);
		}
		
		return ResolveChainLinks(node, operands);
	}
	
	private static bool MixesCharWithInteger(TypeSymbol left, TypeSymbol right) =>
		left == NativeSymbols.Char && right is IntegerType { Kind: not PrimitiveTypeKind.Char } ||
		right == NativeSymbols.Char && left is IntegerType { Kind: not PrimitiveTypeKind.Char };
	
	private IResolvedExpressionNode DefaultLiteral(IResolvedExpressionNode operand) =>
		operand is ResolvedLiteralExpressionNode { Type: UntypedType } ? MaterializeAsDefault(operand) : operand;
	
	private IResolvedExpressionNode ResolveChainLinks(ChainedExpressionNode node,
		List<IResolvedExpressionNode> operands)
	{
		if (AnyInvalid([..operands]))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var links = new List<ChainLink>(node.Ops.Length);
		for (var i = 0; i < node.Ops.Length; i++)
		{
			var op = node.Ops[i];
			var args = new[] { operands[i], operands[i + 1] };
			var (source, range) = args[0].Syntax.SourceLocation;
			var location = new SourceLocation(source, range.Join(args[1].Syntax.SourceLocation.Range));
			if (IsMixedSignComparison(op.Type, args[0].Type, args[1].Type))
			{
				links.Add(new ChainLink(new NativeImpl(op.Type, NativeSymbols.Bool, args[0].Type, args[1].Type), null));
				continue;
			}
			
			var declared = FindOperators(args[0].Type, op.Text).ToList();
			if (args[1].Type != args[0].Type)
				declared.AddRange(FindOperators(args[1].Type, op.Text));
			
			ICallable[] visible =
			[
				..declared
					.Where(entry => CanAccess(entry.Owner, entry.Method.Function))
					.Select(entry => new FunctionCallable(GetFunctionInfo(entry.Method, entry.Owner)))
			];
			
			if (declared.Count > 0 && visible.Length == 0)
				return Error(node, ReportHiddenMember(op.SourceLocation, op.Text,
					declared.Select(static entry => entry.Method.Function)), CurrentTargetType);
			
			var resolutionSet = visible.Length > 0
				? ResolveCallable(visible, args, MaterializationMode.Overload)
				: default;
			
			if (visible.Length == 0 || !resolutionSet.HasResult && !resolutionSet.IsAmbiguous)
			{
				if (ResolveBuiltInLink(op, args) is not { } builtIn)
					return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
				
				resolutionSet = builtIn;
			}
			
			if (resolutionSet.IsAmbiguous && MixesCharWithInteger(args[0].Type, args[1].Type))
				return Error(node, DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, args[0], op, args[1]),
					CurrentTargetType);
			
			if (resolutionSet.IsAmbiguous)
				return Error(node,
					$"Operation '{op.Text}' between '{args[0].Type.Name}' and '{args[1].Type.Name}' is ambiguous",
					CurrentTargetType, location);
			
			if (!resolutionSet.HasResult)
				return Error(node, DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, args[0], op, args[1]),
					CurrentTargetType);
			
			var resolution = resolutionSet[0];
			if (resolution.Callable.ReturnType != NativeSymbols.Bool)
				return Error(node, $"Cannot chain '{op.Text}' results of type '{resolution.Callable.ReturnType.Name}'",
					CurrentTargetType, location);
			
			for (var j = 0; j < 2; j++)
				operands[i + j] = MaterializeChainOperand(args[j], resolution.Callable.ParameterTypes[j]);
			
			var function = resolution.Callable is FunctionCallable callable ? callable.Info : (FunctionInfo?)null;
			if (FindSharedMove(operands, i, function, resolution.ArgumentConversions) is { } shared)
				return Error(node, "Cannot move operands shared by two comparisons", CurrentTargetType,
					shared.Syntax.SourceLocation);
			
			if (function is { } used)
				TrackFunctionUse(used, node);
			
			links.Add(new ChainLink(resolution.Callable as OperationImpl, function)
			{
				LeftConversion = resolution.ArgumentConversions[0],
				RightConversion = resolution.ArgumentConversions[1]
			});
		}
		
		return new ResolvedChainedExpressionNode(NativeSymbols.Bool, operands, links, node);
	}
	
	private ResolutionSet? ResolveBuiltInLink(Token op, IResolvedExpressionNode[] args)
	{
		if (args[0].Type is PointerType { BaseType: var leftBase } &&
		    args[1].Type is PointerType { BaseType: var rightBase } &&
		    leftBase != rightBase && leftBase != NativeSymbols.Void && rightBase != NativeSymbols.Void)
		{
			Diagnostics.Add(DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, args[0], op, args[1]));
			return null;
		}
		
		return ResolveCallable(_operatorRegistry.GetBinaryCandidates(op.Type), args, MaterializationMode.Overload,
			NativeSymbols.Bool);
	}
	
	private IResolvedExpressionNode MaterializeChainOperand(IResolvedExpressionNode operand, TypeSymbol target)
	{
		if (operand.Type is not UntypedType)
			return operand;
		
		var materialized = MaterializeExpression(operand, target);
		return materialized.Type is UntypedType ? MaterializeAsDefault(materialized) : materialized;
	}
	
	private IResolvedExpressionNode? FindSharedMove(List<IResolvedExpressionNode> operands, int link,
		FunctionInfo? function, IReadOnlyList<Conversion?> conversions)
	{
		for (var j = 0; j < 2; j++)
		{
			var index = link + j;
			var moves = function?.Signature.GetMode(j) == ParameterMode.Own ||
			            conversions[j] is FunctionConversion { Function.Signature: var signature } &&
			            signature.GetMode(0) == ParameterMode.Own;
			
			if (index > 0 && index < operands.Count - 1 && moves && !_typePool.IsCopy(operands[index].Type))
				return operands[index];
		}
		
		return null;
	}
	
	private static bool IsBorrowedFunctionParameter(IResolvedExpressionNode node) =>
		node is ResolvedVarExpressionNode { Symbol: ParameterSymbol { Mode: ParameterMode.ReadOnly } } &&
		node.Type is FunctionType { IsRef: true, IsExternal: false };
	
	private bool IsMovedFunctionParameter(IResolvedExpressionNode node, TypeSymbol target) =>
		IsBorrowedFunctionParameter(node) && node.Type is FunctionType function &&
		_typePool.GetPlainFunctionType(function) == target;
	
	private static string GetDisplayName(IResolvedExpressionNode node) =>
		IsBorrowedFunctionParameter(node) && node.Type is FunctionType function ? function.PlainName : node.Type.Name;
	
	private static TypeSymbol? FindNullableOperand(IResolvedExpressionNode left, IResolvedExpressionNode right) =>
		(left.Type, right.Type) switch
		{
			(TypeParameterSymbol { HasNull: true } type, UntypedNullType) => type,
			(UntypedNullType, TypeParameterSymbol { HasNull: true } type) => type,
			_ => null
		};
}