using System.Numerics;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private void EmitDrop(Value value, LLVMBuilderRef builder)
	{
		while (value is ConversionValue conversion)
			value = conversion.Source;
		
		if (value.Type is DynType)
		{
			EmitDynDrop(EmitAddress(value, builder), builder);
			return;
		}
		
		LLVMValueRef address;
		if (IsAddressable(value))
		{
			address = EmitAddress(value, builder);
		}
		else
		{
			address = SpillValue(builder, value.Type, EmitValue(value, builder), "dropped");
		}
		
		EmitDropCall(value.Type, address, builder);
	}
	
	private void EmitDropCall(TypeSymbol type, LLVMValueRef address, LLVMBuilderRef builder)
	{
		type = Substitute(type);
		if (_typePool.NeedsDrop(type))
			builder.BuildCall2(DropGlueType, GetDropGlue(type), [address]);
	}
	
	private LLVMValueRef GetDropGlue(TypeSymbol type)
	{
		if (current.DropGlue.TryGetValue(type, out var glue))
			return glue;
		
		glue = current.Module.AddFunction($"drop${type.Name}", DropGlueType);
		glue.Linkage = LLVMLinkage.LLVMInternalLinkage;
		current.DropGlue[type] = glue;
		
		using var builder = CreateGeneratedBuilder(glue, DescribeDropGlue(type), type switch
		{
			RecordSymbol record => record.Node.Identifier.SourceLocation,
			EnumSymbol enumType => enumType.Node.Identifier.SourceLocation,
			_ => SourceLocation.None
		});
		
		var address = glue.GetParam(0);
		switch (type)
		{
			case RecordSymbol record:
				EmitRecordDropGlue(record, address, builder);
				break;
			
			case EnumSymbol enumType:
				EmitEnumDropGlue(enumType, address, builder);
				break;
			
			case ArrayType array:
				EmitArrayDropGlue(array, address, builder);
				break;
			
			case ClosureType closure:
				EmitClosureDropGlue(closure, address, builder);
				break;
		}
		
		builder.BuildRetVoid();
		return glue;
	}
	
	private void EmitRecordDropGlue(RecordSymbol record, LLVMValueRef address, LLVMBuilderRef builder)
	{
		if (_typePool.GetDestructor(record) is { } destructor)
		{
			GetFunctionValue(destructor);
			var function = current.Functions[destructor];
			builder.BuildCall2(function.FunctionType, function.FunctionValue, [address]);
		}
		
		var recordType = MapTypeSymbol(record);
		foreach (var field in record.Members.OfType<FieldSymbol>().Reverse())
		{
			var fieldType = _typePool.GetTypeOfMember(field);
			if (!_typePool.NeedsDrop(fieldType) || _typePool.IsMovedByDestructor(record, field))
				continue;
			
			var index = (uint)_typePool.GetFieldIndex(record, field);
			EmitDropCall(fieldType, builder.BuildStructGEP2(recordType, address, index, field.Name), builder);
		}
	}
	
	private void EmitClosureDropGlue(ClosureType closure, LLVMValueRef address, LLVMBuilderRef builder)
	{
		var closureType = MapTypeSymbol(closure);
		for (var i = closure.CaptureTypes.Length - 1; i >= 0; i--)
		{
			if (_typePool.NeedsDrop(closure.CaptureTypes[i]))
				EmitDropCall(closure.CaptureTypes[i], builder.BuildStructGEP2(closureType, address, (uint)i, "capture"),
					builder);
		}
	}
	
	private void EmitEnumDropGlue(EnumSymbol enumType, LLVMValueRef address, LLVMBuilderRef builder)
	{
		var glue = builder.InsertBlock.Parent;
		var type = MapTypeSymbol(enumType);
		var done = _context.AppendBasicBlock(glue, "done");
		var droppedCases = enumType.Cases
			.Where(enumCase => enumCase.Fields.Any(field => _typePool.NeedsDrop(_typePool.GetTypeOfMember(field))))
			.DistinctBy(enumCase => _typePool.GetCaseValue(enumType, enumCase))
			.ToList();
		
		var tagAddress = builder.BuildStructGEP2(type, address, 0, "tag.addr");
		var tag = builder.BuildLoad2(MapTypeSymbol(_typePool.GetTagType(enumType)), tagAddress, "tag");
		var dispatch = builder.BuildSwitch(tag, done, (uint)droppedCases.Count);
		foreach (var enumCase in droppedCases)
		{
			var block = _context.AppendBasicBlock(glue, enumCase.Name);
			dispatch.AddCase(EmitCaseTag(enumType, enumCase), block);
			builder.PositionAtEnd(block);
			
			var payload = builder.BuildStructGEP2(type, address, 1, "payload");
			var payloadType = GetPayloadType(enumCase);
			for (var i = enumCase.Fields.Length - 1; i >= 0; i--)
			{
				var field = enumCase.Fields[i];
				var fieldType = _typePool.GetTypeOfMember(field);
				if (_typePool.NeedsDrop(fieldType))
					EmitDropCall(fieldType, builder.BuildStructGEP2(payloadType, payload, (uint)i, field.Name),
						builder);
			}
			
			builder.BuildBr(done);
		}
		
		builder.PositionAtEnd(done);
	}
	
	private void EmitArrayDropGlue(ArrayType array, LLVMValueRef address, LLVMBuilderRef builder) =>
		EmitElementDrops(array, address, BigInteger.Zero, array.Length, builder);
	
	private void EmitElementDrops(DropElementsInstruction instruction, LLVMBuilderRef builder)
	{
		var array = (ArrayType)Substitute(instruction.Array.Type);
		var available = BigInteger.Max(array.Length - instruction.Start, BigInteger.Zero);
		var count = BigInteger.Min(instruction.Count ?? available, available);
		if (count.IsZero)
			return;
		
		LLVMValueRef address;
		if (IsAddressable(instruction.Array))
		{
			address = EmitAddress(instruction.Array, builder);
		}
		else
		{
			address = SpillValue(builder, array, EmitValue(instruction.Array, builder), "dropped");
		}
		
		var flags = instruction.Flags is { } flagArray
			? ((ArrayType)Substitute(flagArray.Type), EmitAddress(flagArray, builder))
			: ((ArrayType, LLVMValueRef)?)null;
		
		EmitElementDrops(array, address, instruction.Start, count, builder, flags);
	}
	
	private void EmitElementDrops(ArrayType array, LLVMValueRef address, BigInteger start, BigInteger count,
		LLVMBuilderRef builder, (ArrayType Type, LLVMValueRef Address)? flags = null)
	{
		if (count.IsZero || !_typePool.NeedsDrop(Substitute(array.ElementType)))
			return;
		
		var function = builder.InsertBlock.Parent;
		var entry = builder.InsertBlock;
		var loop = _context.AppendBasicBlock(function, "loop");
		var done = _context.AppendBasicBlock(function, "done");
		var indexType = MapTypeSymbol(NativeSymbols.UIntSize);
		builder.BuildBr(loop);
		
		builder.PositionAtEnd(loop);
		var index = builder.BuildPhi(indexType, "index");
		var element = builder.BuildSub(index, LLVMValueRef.CreateConstInt(indexType, 1), "element");
		var zero = LLVMValueRef.CreateConstInt(indexType, 0);
		var first = LLVMValueRef.CreateConstInt(indexType, (ulong)start);
		var elementAddress = builder.BuildGEP2(MapTypeSymbol(array), address, new[] { zero, element }, "elemptr");
		if (flags is var (flagType, flagAddress))
		{
			var flagPointer =
				builder.BuildGEP2(MapTypeSymbol(flagType), flagAddress, new[] { zero, element }, "flagptr");
			
			var dropElement = _context.AppendBasicBlock(function, "drop_element");
			var nextElement = _context.AppendBasicBlock(function, "next_element");
			builder.BuildCondBr(builder.BuildLoad2(_context.Int1Type, flagPointer, "flag"), dropElement, nextElement);
			builder.PositionAtEnd(dropElement);
			EmitDropCall(array.ElementType, elementAddress, builder);
			builder.BuildBr(nextElement);
			builder.PositionAtEnd(nextElement);
		}
		else
		{
			EmitDropCall(array.ElementType, elementAddress, builder);
		}
		
		builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, element, first), loop, done);
		index.AddIncoming([LLVMValueRef.CreateConstInt(indexType, (ulong)(start + count)), element],
			[entry, builder.InsertBlock], 2);
		
		builder.PositionAtEnd(done);
	}
}