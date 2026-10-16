using System.Text;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private LLVMValueRef EmitPointerOffset(PointerOffsetValue v, LLVMBuilderRef builder)
	{
		var ptr = EmitValue(v.Pointer, builder);
		var offset = EmitValue(v.Offset, builder);
		
		if (v.Op == BinaryOperation.Subtraction)
			offset = offset.IsConstant
				? LLVMValueRef.CreateConstNeg(offset)
				: builder.BuildNeg(offset, "ptroff.negate");
		
		var ptrType = (PointerType)v.Type;
		
		var elementType = ptrType.BaseType == NativeSymbols.Void
			? _context.Int8Type
			: MapTypeSymbol(ptrType.BaseType);
		
		if (ptrType.BaseType != NativeSymbols.Void)
			return builder.BuildGEP2(elementType, ptr, new[] { offset }, "ptroff");
		
		var bytePtrType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
		var bytePtr = builder.BuildBitCast(ptr, bytePtrType, "voidptr.as.byteptr");
		var result = builder.BuildGEP2(elementType, bytePtr, new[] { offset }, "ptroff");
		return builder.BuildBitCast(result, MapTypeSymbol(ptrType), "byteptr.as.voidptr");
	}
	
	private LLVMValueRef EmitPointerDifference(PointerDifferenceValue v, LLVMBuilderRef builder)
	{
		var isize = MapTypeSymbol(NativeSymbols.IntSize);
		var left = builder.BuildPtrToInt(EmitValue(v.Left, builder), isize, "ptrdiff.left");
		var right = builder.BuildPtrToInt(EmitValue(v.Right, builder), isize, "ptrdiff.right");
		
		var byteDiff = builder.BuildSub(left, right, "ptrdiff.bytes");
		
		if (v.PointerType.BaseType == NativeSymbols.Void)
			return byteDiff;
		
		var element = Substitute(v.PointerType.BaseType);
		var elementBits = _typePool.SizeTable.GetSize(element).CountBits(_pointerSize * 8);
		var elementBytes = (elementBits + 7) / 8;
		
		if (elementBytes == 0)
		{
			PanicIf(_true, $"cannot subtract pointers to zero-size type '{element.Name}'", v.SourceLocation, builder);
			return LLVMValueRef.CreateConstNull(isize);
		}
		
		var elementSize = EmitSizeConstant(elementBytes, false);
		return builder.BuildSDiv(byteDiff, elementSize, "ptrdiff.typed");
	}
	
	private LLVMValueRef EmitBinaryOp(BinOpValue v, LLVMBuilderRef builder)
	{
		if (v.Op is BinaryOperation.ShiftLeft or BinaryOperation.ShiftRight)
			return EmitShift(v, builder);
		
		if (v.Op is BinaryOperation.RotateLeft or BinaryOperation.RotateRight)
			return EmitRotate(v, builder);
		
		if (v.Left.Type is FloatType)
			return EmitFloatBinaryOp(v, builder);
		
		if (v.Op is BinaryOperation.Addition or BinaryOperation.Subtraction &&
		    (Substitute(v.Left.Type) == NativeSymbols.Char || Substitute(v.Right.Type) == NativeSymbols.Char))
			return EmitCharArithmetic(v, builder);
		
		var left = EmitValue(v.Left, builder);
		var right = EmitValue(v.Right, builder);
		if (Substitute(v.Left.Type) is EnumSymbol { IsMatch: true } matchEnum)
		{
			left = EmitMatchTag(matchEnum, left, builder);
			right = EmitMatchTag(matchEnum, right, builder);
		}
		
		var signed = v.Left.Type is IntegerType { IsSigned: true };
		
		if (v.Left.Type is IntegerType { IsSigned: var leftSigned } &&
		    v.Right.Type is IntegerType { IsSigned: var rightSigned } &&
		    leftSigned != rightSigned)
		{
			var wide = _context.GetIntType(Math.Max(left.TypeOf.IntWidth, right.TypeOf.IntWidth) + 1);
			left = leftSigned ? builder.BuildSExt(left, wide) : builder.BuildZExt(left, wide);
			right = rightSigned ? builder.BuildSExt(right, wide) : builder.BuildZExt(right, wide);
			signed = true;
		}
		
		return v switch
		{
			{ Op: BinaryOperation.Addition or BinaryOperation.Subtraction or BinaryOperation.Multiplication }
				when checks.Overflow =>
				EmitCheckedArithmetic(v.Op, left, right, signed, v.SourceLocation, builder),
			
			{ IsConstant: true, Op: BinaryOperation.Addition or BinaryOperation.WrappingAddition } =>
				LLVMValueRef.CreateConstAdd(left, right),
			{ Op: BinaryOperation.Addition or BinaryOperation.WrappingAddition } =>
				builder.BuildAdd(left, right),
			
			{ IsConstant: true, Op: BinaryOperation.Subtraction or BinaryOperation.WrappingSubtraction } =>
				LLVMValueRef.CreateConstSub(left, right),
			{ Op: BinaryOperation.Subtraction or BinaryOperation.WrappingSubtraction } =>
				builder.BuildSub(left, right),
			
			{ Op: BinaryOperation.Multiplication or BinaryOperation.WrappingMultiplication } =>
				builder.BuildMul(left, right),
			
			{ Op: BinaryOperation.Division or BinaryOperation.Modulo } =>
				EmitIntegerDivision(v.Op, left, right, signed, v.SourceLocation, builder),
			
			{ Op: BinaryOperation.Greater } => signed
				? builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, left, right)
				: builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, left, right),
			
			{ Op: BinaryOperation.GreaterEqual } => signed
				? builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, left, right)
				: builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, left, right),
			
			{ Op: BinaryOperation.Less } => signed
				? builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, left, right)
				: builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, left, right),
			
			{ Op: BinaryOperation.LessEqual } => signed
				? builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, left, right)
				: builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, left, right),
			
			{ Op: BinaryOperation.Equal } =>
				builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right),
			
			{ Op: BinaryOperation.NotEqual } =>
				builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right),
			
			{ Op: BinaryOperation.BitwiseAnd } =>
				builder.BuildAnd(left, right),
			
			{ Op: BinaryOperation.BitwiseOr } =>
				builder.BuildOr(left, right),
			
			{ Op: BinaryOperation.BitwiseXor } =>
				builder.BuildXor(left, right),
			
			{ Op: BinaryOperation.LogicalAnd } =>
				builder.BuildAnd(left, right),
			
			{ Op: BinaryOperation.LogicalOr } =>
				builder.BuildOr(left, right),
			
			_ => throw new InvalidOperationException()
		};
	}
	
	private LLVMValueRef EmitCharArithmetic(BinOpValue v, LLVMBuilderRef builder)
	{
		var left = WidenCharOperand(v.Left, builder);
		var right = WidenCharOperand(v.Right, builder);
		var result = v.Op == BinaryOperation.Addition
			? builder.BuildAdd(left, right, "char.add")
			: builder.BuildSub(left, right, "char.sub");
		
		if (Substitute(v.Type) == NativeSymbols.Char && checks.Overflow)
			PanicIf(builder.BuildNot(IsScalarValue(result, builder), "invalid"), "char overflow", v.SourceLocation,
				builder);
		
		return builder.BuildTrunc(result, _context.Int32Type, "char");
	}
	
	private LLVMValueRef WidenCharOperand(Value operand, LLVMBuilderRef builder)
	{
		var value = EmitValue(operand, builder);
		return Substitute(operand.Type) is IntegerType { IsSigned: true }
			? builder.BuildSExt(value, _context.Int64Type, "wide")
			: builder.BuildZExt(value, _context.Int64Type, "wide");
	}
	
	private LLVMValueRef IsScalarValue(LLVMValueRef value, LLVMBuilderRef builder)
	{
		var type = value.TypeOf;
		var inRange = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, value,
			LLVMValueRef.CreateConstInt(type, 0x10FFFF), "in.range");
		
		var offset = builder.BuildSub(value, LLVMValueRef.CreateConstInt(type, 0xD800), "surrogate.offset");
		var outside = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, offset, LLVMValueRef.CreateConstInt(type, 0x7FF),
			"not.surrogate");
		
		return builder.BuildAnd(inRange, outside, "scalar");
	}
	
	private LLVMValueRef EmitCheckedArithmetic(BinaryOperation op, LLVMValueRef left, LLVMValueRef right, bool signed,
		SourceLocation location, LLVMBuilderRef builder)
	{
		var type = left.TypeOf;
		var (name, message) = op switch
		{
			BinaryOperation.Addition => ("add", "addition overflow"),
			BinaryOperation.Subtraction => ("sub", "subtraction overflow"),
			_ => ("mul", "multiplication overflow")
		};
		
		var resultType = _context.GetStructType([type, _context.Int1Type], false);
		var functionType = LLVMTypeRef.CreateFunction(resultType, [type, type]);
		var intrinsic = GetIntrinsic($"llvm.{(signed ? 's' : 'u')}{name}.with.overflow.i{type.IntWidth}", functionType);
		var result = builder.BuildCall2(functionType, intrinsic, [left, right]);
		PanicIf(builder.BuildExtractValue(result, 1), message, location, builder);
		return builder.BuildExtractValue(result, 0);
	}
	
	private LLVMValueRef EmitCheckedNegation(UnaryOpValue v, LLVMBuilderRef builder)
	{
		var operand = EmitValue(v.Operand, builder);
		var type = operand.TypeOf;
		var minimum = builder.BuildShl(LLVMValueRef.CreateConstInt(type, 1),
			LLVMValueRef.CreateConstInt(type, type.IntWidth - 1));
		
		PanicIf(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, operand, minimum), "negation overflow", v.SourceLocation,
			builder);
		
		return builder.BuildNeg(operand);
	}
	
	private LLVMValueRef EmitIntegerDivision(BinaryOperation op, LLVMValueRef left, LLVMValueRef right, bool signed,
		SourceLocation location, LLVMBuilderRef builder)
	{
		var type = left.TypeOf;
		var one = LLVMValueRef.CreateConstInt(type, 1);
		PanicIf(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right, LLVMValueRef.CreateConstNull(type)),
			"division by zero", location, builder);
		
		if (!signed)
		{
			return op == BinaryOperation.Division
				? builder.BuildUDiv(left, right)
				: builder.BuildURem(left, right);
		}
		
		var isMinusOne = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right, LLVMValueRef.CreateConstAllOnes(type));
		if (isMinusOne.Handle == _false.Handle)
		{
			return op == BinaryOperation.Division
				? builder.BuildSDiv(left, right)
				: builder.BuildSRem(left, right);
		}
		
		if (op == BinaryOperation.Modulo)
			return builder.BuildSRem(left, builder.BuildSelect(isMinusOne, one, right));
		
		var minimum = builder.BuildShl(one, LLVMValueRef.CreateConstInt(type, type.IntWidth - 1));
		PanicIf(builder.BuildAnd(isMinusOne, builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, minimum)),
			"division overflow", location, builder);
		
		return builder.BuildSDiv(left, right);
	}
	
	private void PanicIf(LLVMValueRef condition, string message, SourceLocation location, LLVMBuilderRef builder)
	{
		if (condition.Handle == _false.Handle)
			return;
		
		var function = builder.InsertBlock.Parent;
		var panicBlock = _context.AppendBasicBlock(function, "panic");
		var continueBlock = _context.AppendBasicBlock(function, "no_panic");
		builder.BuildCondBr(condition, panicBlock, continueBlock);
		
		builder.PositionAtEnd(panicBlock);
		if (_inputs.PanicAdapters.TryGetValue(location.Source.FilePath, out var adapter))
			EmitValue(new CallValue(adapter, [CreateStr(message), CreateStr(DescribeLocation(location))], location),
				builder);
		else
			builder.BuildCall2(TrapType, GetTrapFunction(), []);
		
		builder.BuildUnreachable();
		
		builder.PositionAtEnd(continueBlock);
	}
	
	private string DescribeLocation(SourceLocation location)
	{
		var file = location.Source.FilePath;
		var path = Path.IsPathFullyQualified(file) &&
		           (_config.ProjectRoots.Where(root => Contains(root, file)).MaxBy(static root => root.Length) ??
		            _config.SourceRoot) is { } root
			? Path.GetRelativePath(root, file)
			: file;
		
		var (line, column) = location.GetLineColumn();
		return $"{path.Replace('\\', '/')}:{line}:{column}";
	}
	
	private static bool Contains(string root, string path) =>
		Path.GetRelativePath(root, path) is var relative && !Path.IsPathRooted(relative) && relative != ".." &&
		!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
	
	private LLVMValueRef GetTrapFunction() => GetIntrinsic("llvm.trap", TrapType);
	
	private static ConstantValue CreateStr(string text)
	{
		var bytes = Encoding.UTF8.GetBytes(text);
		return new ConstantValue(NativeSymbols.Str, new StrValue((ulong)bytes.Length, bytes));
	}
	
	private LLVMValueRef GetCFunction(string name, LLVMTypeRef type)
	{
		var function = current.Module.GetNamedFunction(name);
		if (function.Handle != IntPtr.Zero)
			return function;
		
		function = current.Module.AddFunction(name, type);
		function.Linkage = LLVMLinkage.LLVMExternalLinkage;
		return function;
	}
	
	private LLVMValueRef EmitFloatBinaryOp(BinOpValue v, LLVMBuilderRef builder)
	{
		var left = EmitValue(v.Left, builder);
		var right = EmitValue(v.Right, builder);
		return v.Op switch
		{
			BinaryOperation.Addition => builder.BuildFAdd(left, right),
			BinaryOperation.Subtraction => builder.BuildFSub(left, right),
			BinaryOperation.Multiplication => builder.BuildFMul(left, right),
			BinaryOperation.Division => builder.BuildFDiv(left, right),
			BinaryOperation.Modulo => builder.BuildFRem(left, right),
			BinaryOperation.Equal => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right),
			BinaryOperation.NotEqual => builder.BuildFCmp(LLVMRealPredicate.LLVMRealUNE, left, right),
			BinaryOperation.Greater => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right),
			BinaryOperation.GreaterEqual => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right),
			BinaryOperation.Less => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right),
			BinaryOperation.LessEqual => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right),
			_ => throw new InvalidOperationException()
		};
	}
	
	private LLVMValueRef EmitShift(BinOpValue v, LLVMBuilderRef builder)
	{
		var value = EmitValue(v.Left, builder);
		var amount = EmitValue(v.Right, builder);
		var type = value.TypeOf;
		var bits = type.IntWidth;
		var count = ResizeInteger(amount, type, v.Right.Type is IntegerType { IsSigned: true }, builder);
		
		var checkedAmount = amount.TypeOf.IntWidth > bits ? amount : count;
		var inRange = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, checkedAmount,
			LLVMValueRef.CreateConstInt(checkedAmount.TypeOf, bits));
		
		var isSigned = v.Left.Type is IntegerType { IsSigned: true };
		var shifted = v.Op switch
		{
			BinaryOperation.ShiftLeft => builder.BuildShl(value, count),
			_ when isSigned => builder.BuildAShr(value, count),
			_ => builder.BuildLShr(value, count)
		};
		
		var shiftedOut = v.Op == BinaryOperation.ShiftRight && isSigned
			? builder.BuildAShr(value, LLVMValueRef.CreateConstInt(type, bits - 1))
			: LLVMValueRef.CreateConstNull(type);
		
		return builder.BuildSelect(inRange, shifted, shiftedOut);
	}
	
	private LLVMValueRef EmitRotate(BinOpValue v, LLVMBuilderRef builder)
	{
		var value = EmitValue(v.Left, builder);
		var type = value.TypeOf;
		var amount = ResizeInteger(EmitValue(v.Right, builder), type,
			v.Right.Type is IntegerType { IsSigned: true }, builder);
		
		var mask = LLVMValueRef.CreateConstInt(type, type.IntWidth - 1);
		var forward = builder.BuildAnd(amount, mask);
		var backward = builder.BuildAnd(builder.BuildNeg(amount), mask);
		var (leftCount, rightCount) = v.Op == BinaryOperation.RotateLeft
			? (forward, backward)
			: (backward, forward);
		
		return builder.BuildOr(builder.BuildShl(value, leftCount), builder.BuildLShr(value, rightCount));
	}
	
	private LLVMValueRef EmitUnaryOp(UnaryOpValue v, LLVMBuilderRef builder) => v switch
	{
		{ Op: UnaryOperation.Identity } => EmitValue(v.Operand, builder),
		{ Op: UnaryOperation.Negation, Operand.Type: FloatType } => builder.BuildFNeg(EmitValue(v.Operand, builder)),
		{ IsConstant: true, Op: UnaryOperation.Negation } => LLVMValueRef.CreateConstNeg(EmitValue(v.Operand, builder)),
		{ Op: UnaryOperation.Negation } when checks.Overflow => EmitCheckedNegation(v, builder),
		{ Op: UnaryOperation.Negation } => builder.BuildNeg(EmitValue(v.Operand, builder)),
		
		{ IsConstant: true, Op: UnaryOperation.BitwiseNot } =>
			LLVMValueRef.CreateConstNot(EmitValue(v.Operand, builder)),
		{ Op: UnaryOperation.BitwiseNot } => builder.BuildNot(EmitValue(v.Operand, builder)),
		
		{ IsConstant: true, Op: UnaryOperation.LogicalNot } =>
			LLVMValueRef.CreateConstNot(EmitValue(v.Operand, builder)),
		{ Op: UnaryOperation.LogicalNot } => builder.BuildNot(EmitValue(v.Operand, builder)),
		
		{ Op: UnaryOperation.AddressOf } => EmitAddress(v.Operand, builder),
		{ Op: UnaryOperation.Dereference } => LoadValue(builder, v.Type, EmitValue(v.Operand, builder), "deref"),
		
		_ => throw new InvalidOperationException()
	};
}