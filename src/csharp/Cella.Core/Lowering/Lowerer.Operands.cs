using Cella.Core.Binding;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed partial class Lowerer
{
	private sealed partial class FunctionLowerer
	{
		private static bool IsShortCircuitOp(OperationImpl? op) =>
			op is NativeImpl { Op: TokenType.OpAmpersandAmpersand or TokenType.OpBarBar };
		
		private VariableValue LowerShortCircuit(IResolvedExpressionNode leftNode, TokenType op,
			IResolvedExpressionNode rightNode)
		{
			var sourceLocation = Join(leftNode.Syntax.SourceLocation, rightNode.Syntax.SourceLocation);
			
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "sc_result");
			GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(resultSymbol, new UndefValue(NativeSymbols.Bool),
				leftNode.Syntax.SourceLocation, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool), sourceLocation);
			
			var rightBlock = CreateBlock("sc_right");
			var skipBlock = CreateBlock("sc_skip");
			var mergeBlock = CreateBlock("sc_merge");
			
			var isAnd = op == TokenType.OpAmpersandAmpersand;
			LowerBranch(leftNode, isAnd ? rightBlock : skipBlock, isAnd ? skipBlock : rightBlock);
			
			skipBlock.Instructions.Add(new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result,
				isAnd ? ConstantValue.False : ConstantValue.True, sourceLocation)));
			
			skipBlock.SetTerminator(new BranchTerminator(mergeBlock, leftNode.Syntax.SourceLocation));
			
			currentBlock = rightBlock;
			var right = VisitNode(rightNode);
			
			if (currentBlock is not null)
			{
				currentBlock.Instructions.Add(
					new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result, right, sourceLocation)));
				
				currentBlock.FillTerminator(new BranchTerminator(mergeBlock, rightNode.Syntax.SourceLocation));
			}
			
			ContinueWith(mergeBlock);
			return result;
		}
		
		private LocalVariableSymbol CreateTempSymbol(TypeSymbol type, string name)
		{
			name = $".t{NextTempId()}__{name}";
			var symbol = new LocalVariableSymbol(new(TokenType.Identifier, SourceLocation.None, name), type, true);
			_temporaries.Add(symbol);
			return symbol;
		}
		
		private enum Passing
		{
			Read,
			Borrow,
			Consume
		}
		
		private List<Value> LowerOperands(IReadOnlyList<IResolvedExpressionNode> operands,
			Func<int, Passing>? passing = null, bool hasReceiver = false, bool isPlaceOperator = false)
		{
			var values = new List<Value>(operands.Count);
			var passings = new List<Passing>(operands.Count);
			var deferred = new List<bool>(operands.Count);
			for (var i = 0; i < operands.Count; i++)
			{
				var operand = operands[i];
				passings.Add(passing?.Invoke(i) ?? Passing.Read);
				deferred.Add(DefersAccess(operand, passings[i], i == 0 && isPlaceOperator));
				if (MayEmit(operand) || passings[i] == Passing.Borrow && !IsPlace(operand))
					SettleOperands(values, passings, deferred);
				
				values.Add(passings[i] switch
				{
					Passing.Borrow => LowerBorrow(operand),
					Passing.Consume => Consume(VisitNode(operand), (i > 0 || !hasReceiver) && NamesPlace(operand)),
					_ => VisitNode(operand)
				});
			}
			
			if (values.Count > 1 &&
			    Enumerable.Range(0, values.Count - 1).Any(i => deferred[i] && HasAccessWork(values[i])))
				SettleOperands(values, passings, deferred);
			
			return values;
		}
		
		private static bool DefersAccess(IResolvedExpressionNode operand, Passing passing, bool isReceiver) =>
			isReceiver || passing == Passing.Borrow || operand is ResolvedMutArgumentExpressionNode;
		
		private void SettleOperands(List<Value> values, List<Passing> passings, List<bool> deferred)
		{
			for (var i = 0; i < values.Count; i++)
				values[i] = SettleOperand(values[i], passings[i], deferred[i]);
		}
		
		private List<Value> LowerArguments(IReadOnlyList<IResolvedExpressionNode> arguments, FunctionInfo function,
			int firstParameter) => LowerOperands(arguments, GetArgumentPassing(function, firstParameter),
			firstParameter == 0 && function.Symbol.Kind == FunctionKind.Method,
			firstParameter == 0 && IsPlaceOperator(function));
		
		private Func<int, Passing> GetArgumentPassing(FunctionInfo function, int firstParameter)
		{
			var signature = function.DeclaredSignature;
			return i => firstParameter + i < signature.ParameterTypes.Length
				? GetPassing(signature.GetMode(firstParameter + i))
				: Passing.Read;
		}
		
		private static Passing GetPassing(ParameterMode mode) =>
			TypePool.PassesByPointer(mode) ? Passing.Borrow
			: mode == ParameterMode.Own ? Passing.Consume
			: Passing.Read;
		
		private Value Consume(Value value, bool isImplicit = false) =>
			!_typePool.IsCopy(value.Type) && IsPlaceValue(value)
				? new MoveValue(value) { IsImplicit = isImplicit }
				: value;
		
		private static bool IsPlaceValue(Value value) => value switch
		{
			VariableValue or GlobalValue or UnaryOpValue { Op: UnaryOperation.Dereference } => true,
			AccessValue { Member: FieldSymbol } v => IsPlaceValue(v.Target),
			IndexerValue { Target.Type: StringType } => true,
			IndexerValue v => IsPlaceValue(v.Target),
			EnumPayloadValue v => IsPlaceValue(v.Target),
			_ => false
		};
		
		private UnaryOpValue LowerBorrow(IResolvedExpressionNode argument)
		{
			var value = VisitNode(argument);
			var location = argument.Syntax.SourceLocation;
			var place = IsPlaceValue(value) ? value : StoreTemporary(value, "borrow", location);
			return new UnaryOpValue(_typePool.GetPointerType(argument.Type), place, UnaryOperation.AddressOf, location);
		}
		
		private static bool IsPlace(IResolvedExpressionNode node) => node switch
		{
			ResolvedVarExpressionNode => true,
			ResolvedAccessExpressionNode { Member: FieldSymbol } n => IsPlace(n.Target),
			ResolvedIndexerExpressionNode { Target.Type: StringType } => true,
			ResolvedIndexerExpressionNode n => IsPlace(n.Target),
			ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } => true,
			_ => false
		};
		
		private static bool NamesPlace(IResolvedExpressionNode node) =>
			IsPlace(node is ResolvedAssignmentExpressionNode assignment ? assignment.Left : node);
		
		private bool MayEmit(IResolvedExpressionNode node) => node switch
		{
			{ Type: NeverType } => true,
			ResolvedLiteralExpressionNode or ResolvedVarExpressionNode or ResolvedGlobalExpressionNode
				or ResolvedFunctionReferenceExpressionNode or ResolvedUndefExpressionNode
				or ResolvedSizeOfExpressionNode or ResolvedAlignOfExpressionNode
				or ResolvedValueParameterExpressionNode => false,
			ResolvedConversionExpressionNode n => MayEmit(n.Source),
			ResolvedAccessExpressionNode n => IsMaterialized(n.Target) || MayEmit(n.Target),
			ResolvedIndexerExpressionNode n => IsMaterialized(n.Target) || MayEmit(n.Target) || MayEmit(n.Index) ||
			                                   n.Target.Type is ArrayType && HasPlaceAccess(n.Target),
			ResolvedUnaryOpExpressionNode n => MayEmit(n.Operand),
			ResolvedMutArgumentExpressionNode n => MayEmit(n.Place),
			ResolvedBorrowExpressionNode n => !IsPlace(n.Place) || MayEmit(n.Place),
			ResolvedOwnExpressionNode n => MayEmit(n.Value),
			ResolvedBinaryOpExpressionNode n => IsShortCircuitOp(n.Operation) || MayEmit(n.Left) || MayEmit(n.Right),
			ResolvedFunctionCallExpressionNode n => MayEmitOperands(n.Arguments, GetArgumentPassing(n.Function, 0),
				IsPlaceOperator(n.Function)),
			ResolvedIndirectCallExpressionNode n => MayEmitOperands([n.Target, ..n.Arguments],
				GetOperandPassing(n.FunctionType)),
			ResolvedArrayExpressionNode n => n.Values.Any(MayEmit),
			ResolvedFStrExpressionNode n => !n.Values.IsEmpty ||
			                                n.Text is { } text && (!IsPlace(text) || MayEmit(text)),
			ResolvedEnumCaseExpressionNode n => n.Payload.Any(MayEmit),
			_ => true
		};
		
		private bool MayEmitOperands(IReadOnlyList<IResolvedExpressionNode> operands, Func<int, Passing> passing,
			bool isPlaceOperator = false) => operands.Where((operand, i) =>
			passing(i) == Passing.Borrow && !IsPlace(operand) || MayEmit(operand) ||
			i < operands.Count - 1 && DefersAccess(operand, passing(i), i == 0 && isPlaceOperator) &&
			HasPlaceAccess(operand)).Any();
		
		private static bool HasPlaceAccess(IResolvedExpressionNode node) => node switch
		{
			ResolvedMutArgumentExpressionNode n => HasPlaceAccess(n.Place),
			ResolvedIndexerExpressionNode => true,
			ResolvedAccessExpressionNode n => HasPlaceAccess(n.Target),
			ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } => true,
			_ => false
		};
		
		private bool IsMaterialized(IResolvedExpressionNode target) =>
			!IsPlace(target) && _typePool.NeedsDrop(target.Type);
		
		public Value Visit(ResolvedAssignmentExpressionNode node) => LowerAssignment(node, true);
		
		public Value Visit(ResolvedPropertyExpressionNode node) => throw new InvalidOperationException();
		
		public Value Visit(ResolvedPropertyAssignmentExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var receiver = node.Receiver is null ? null : LowerReceiver(node.Receiver);
			if (node.Compound is { } compound)
			{
				var valueIndex = node.Setter.DeclaredSignature.ParameterTypes.Length - 1;
				var passing = GetArgumentPassing(compound, 1);
				var args = LowerArguments([node.Right], compound, 1)
					.Select((value, i) => SettleOperand(value, passing(i), passing(i) == Passing.Borrow))
					.ToList();
				
				receiver = receiver is null ? null : StabilizeStorage(receiver);
				var place = StoreTemporary(new CallValue(node.Getter, PassReceiver(node.Getter, receiver), location),
					"current");
				
				args.Insert(0, new UnaryOpValue(_typePool.GetPointerType(place.Type), place, UnaryOperation.AddressOf,
					location));
				
				GetOrMakeBlock().Instructions.Add(new ExpressionInstruction(new CallValue(compound, args, location)));
				return new CallValue(node.Setter,
					[..PassReceiver(node.Setter, receiver), PassChainArgument(place, node.Setter, valueIndex, false)],
					location);
			}
			
			var right = CaptureAsAtomic(Consume(VisitNode(node.Right)), "operand");
			receiver = receiver is null ? null : StabilizeStorage(receiver);
			var current = new CallValue(node.Getter, PassReceiver(node.Getter, receiver), location);
			var value = LowerBinOp(current, node.Operation!, right, location);
			return new CallValue(node.Setter, [..PassReceiver(node.Setter, receiver), value], location);
		}
		
		private Value LowerReceiver(IResolvedExpressionNode node)
		{
			var value = VisitPlace(node);
			return IsPlaceValue(value) ? StabilizeOperands(value) : StoreTemporary(value, "receiver");
		}
		
		private List<Value> PassReceiver(FunctionInfo accessor, Value? receiver)
		{
			if (receiver is null)
				return [];
			
			var signature = accessor.DeclaredSignature;
			var mode = signature.GetMode(0);
			if (mode != ParameterMode.Mut && GetPassing(mode) != Passing.Borrow)
				return [receiver];
			
			return
			[
				new UnaryOpValue(_typePool.GetPointerType(receiver.Type), receiver, UnaryOperation.AddressOf,
					receiver.SourceLocation)
			];
		}
	}
}