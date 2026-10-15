using System.Numerics;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private void EmitInstruction(LLVMBuilderRef builder, IInstruction instruction)
	{
		var function = builder.InsertBlock.Parent;
		var outermost = _temporaryIndices.TryAdd(function, 0);
		try
		{
			EmitInstructionBody(builder, instruction);
		}
		finally
		{
			if (outermost)
				_temporaryIndices.Remove(function);
		}
	}
	
	private void EmitInstructionBody(LLVMBuilderRef builder, IInstruction instruction)
	{
		SetLocation(builder, instruction.SourceLocation);
		switch (instruction)
		{
			case BeginScopeInstruction i:
				BeginScope(i);
				break;
			
			case EndScopeInstruction i:
				EndScope(i);
				break;
			
			case LocalVarInstruction i:
			{
				var storage = current.Variables[new(i.Symbol, i.Symbol.Type)];
				DeclareLocal(i, storage);
				if (i.Initializer is ZeroValue or DefaultValue or ConstantValue { Value: null } &&
				    IsLarge(i.Symbol.Type))
					EmitZeroFill(builder, MapTypeSymbol(i.Symbol.Type), storage);
				else if (i.Initializer is not UndefValue)
					StoreValue(builder, i.Symbol.Type, EmitValue(i.Initializer, builder), storage);
				
				break;
			}
			
			case ExpressionInstruction i:
			{
				EmitValue(i.Value, builder);
				break;
			}
			
			case DropInstruction i when IsMissingElement(i.Value):
				break;
			
			case DropInstruction { Guard: { } guard } i:
			{
				var function = builder.InsertBlock.Parent;
				var dropBlock = _context.AppendBasicBlock(function, "drop");
				var doneBlock = _context.AppendBasicBlock(function, "drop_done");
				builder.BuildCondBr(EmitValue(guard, builder), dropBlock, doneBlock);
				builder.PositionAtEnd(dropBlock);
				EmitDrop(i.Value, builder);
				builder.BuildBr(doneBlock);
				builder.PositionAtEnd(doneBlock);
				break;
			}
			
			case DropInstruction i:
			{
				EmitDrop(i.Value, builder);
				break;
			}
			
			case DropElementsInstruction { Guard: { } guard } i:
			{
				var function = builder.InsertBlock.Parent;
				var dropBlock = _context.AppendBasicBlock(function, "drop");
				var doneBlock = _context.AppendBasicBlock(function, "drop_done");
				builder.BuildCondBr(EmitValue(guard, builder), dropBlock, doneBlock);
				builder.PositionAtEnd(dropBlock);
				EmitElementDrops(i, builder);
				builder.BuildBr(doneBlock);
				builder.PositionAtEnd(doneBlock);
				break;
			}
			
			case DropElementsInstruction i:
			{
				EmitElementDrops(i, builder);
				break;
			}
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private bool IsMissingElement(Value value) => value switch
	{
		IndexerValue { Index: ConstantValue { Value: BigInteger index } } v
			when Substitute(v.Target.Type) is ArrayType { Length.Sign: >= 0 } array && index >= array.Length => true,
		IndexerValue v => IsMissingElement(v.Target),
		AccessValue v => IsMissingElement(v.Target),
		EnumPayloadValue v => IsMissingElement(v.Target),
		ConversionValue v => IsMissingElement(v.Source),
		_ => false
	};
	
	private void EmitTerminator(LLVMBuilderRef builder, IBlockTerminator terminator,
		Dictionary<BasicBlock, LLVMBasicBlockRef> blockMap)
	{
		var function = builder.InsertBlock.Parent;
		var outermost = _temporaryIndices.TryAdd(function, 0);
		try
		{
			EmitTerminatorBody(builder, terminator, blockMap);
		}
		finally
		{
			if (outermost)
				_temporaryIndices.Remove(function);
		}
	}
	
	private void EmitTerminatorBody(LLVMBuilderRef builder, IBlockTerminator terminator,
		Dictionary<BasicBlock, LLVMBasicBlockRef> blockMap)
	{
		SetLocation(builder, terminator.SourceLocation);
		switch (terminator)
		{
			case ReturnTerminator term:
				if (term.Value is not { } value)
					builder.BuildRetVoid();
				else
					EmitReturn(value.Type, EmitValue(value, builder), builder);
				
				break;
			
			case BranchTerminator term:
				builder.BuildBr(blockMap[term.Target]);
				break;
			
			case ConditionalBranchTerminator term:
				var condition = EmitValue(term.Condition, builder);
				builder.BuildCondBr(condition, blockMap[term.TrueTarget], blockMap[term.FalseTarget]);
				break;
			
			case SwitchTerminator term:
			{
				var selector = EmitValue(term.Value, builder);
				var instruction = builder.BuildSwitch(selector, blockMap[term.DefaultTarget], (uint)term.Cases.Length);
				foreach (var (caseValue, target) in term.Cases)
					instruction.AddCase(
						LLVMValueRef.CreateConstInt(selector.TypeOf, (ulong)(caseValue & ulong.MaxValue)),
						blockMap[target]);
				
				break;
			}
			
			// This happens with an empty function body
			case UndefinedTerminator:
				builder.BuildRetVoid();
				break;
			
			case UnreachableTerminator:
				builder.BuildUnreachable();
				break;
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private void EmitReturn(TypeSymbol type, LLVMValueRef value, LLVMBuilderRef builder)
	{
		if (currentFunction.ResultParameter is { } result)
		{
			StoreValue(builder, type, value, currentFunction.FunctionValue.GetParam(result));
			builder.BuildRetVoid();
			return;
		}
		
		switch (currentFunction.CSignature?.Return)
		{
			case { Kind: CPassKind.Indirect }:
				StoreValue(builder, type, value, currentFunction.FunctionValue.GetParam(0));
				builder.BuildRetVoid();
				break;
			
			case { } pass:
				builder.BuildRet(PassCArgument(pass, type, value, builder));
				break;
			
			default:
				builder.BuildRet(value);
				break;
		}
	}
	
	private LLVMValueRef EmitValue(Value value, LLVMBuilderRef builder) => value switch
	{
		ConstantValue { Value: null } v when IsLarge(v.Type) => EmitZeroed(builder, v.Type, "zero"),
		ConstantValue v => EmitConstant(v),
		ZeroValue or DefaultValue => EmitZeroed(builder, value.Type, "zero"),
		VariableValue v => LoadValue(builder, v.Type, current.Variables[v.Variable], v.Variable.Symbol.Name),
		GlobalValue v => EmitGlobalLoad(v.Global, builder),
		MoveValue v => EmitValue(v.Place, builder),
		BinOpValue v => EmitBinaryOp(v, builder),
		UnaryOpValue v => EmitUnaryOp(v, builder),
		AssignValue v => EmitAssignValue(v, builder),
		IndexerValue v => EmitIndexer(v, builder),
		AccessValue v => EmitAccessValue(v, builder),
		ArrayValue v => EmitArrayValue(v, builder),
		ConversionValue v => EmitConversion(v, builder),
		CallValue v => EmitAt(v.SourceLocation, builder, () => EmitCall(v, builder)),
		FunctionReferenceValue v => EmitFunctionReference(v.Function, v.Type),
		ClosureValue v => EmitClosure(v, builder),
		EnvironmentValue v => EmitEnvironment(v, builder),
		OwnClosureValue v => EmitOwnClosure(v, builder),
		ClosureFieldValue v => EmitClosureField(v, builder),
		IndirectCallValue v => EmitAt(v.SourceLocation, builder, () => EmitIndirectCall(v, builder)),
		PointerOffsetValue v => EmitPointerOffset(v, builder),
		PointerDifferenceValue v => EmitPointerDifference(v, builder),
		EnumValue v => EmitEnumValue(v, builder),
		EnumTagValue v => EmitEnumTag(v, builder),
		EnumPayloadValue v => EmitEnumPayload(v, builder),
		SizeOfValue v => EmitSizeOf(v),
		AlignOfValue v => EmitAlignOf(v),
		ValueParameterValue v => EmitValueParameter(v),
		NewValue v => EmitNew(v, builder),
		FStrValue v => EmitFStr(v, builder),
		FStrPartValue v => EmitFStrPart(v, builder),
		AtomicValue v => EmitAtomic(v, builder),
		_ => throw new InvalidOperationException()
	};
}