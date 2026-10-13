using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Lowering;
using Cella.Core.Symbols;

namespace Cella.Core.CodeGen;

public readonly record struct ExportName(string Name, bool IsData);

public sealed class ExportSurface
{
	private readonly AssemblySymbol _assembly;
	private readonly TypePool _typePool;
	private readonly HashSet<Symbol> _declared;
	private readonly Dictionary<FunctionSymbol, LoweredFunction> _bodies;
	private readonly HashSet<Symbol> _reached = [];
	private readonly HashSet<TypeSymbol> _visitedTypes = [];
	private readonly Queue<Symbol> _pending = [];
	
	public IReadOnlySet<Symbol> Symbols => _reached;
	
	public ExportSurface(AssemblySymbol assembly, TypePool typePool, IEnumerable<LoweredFunction> genericBodies)
	{
		_assembly = assembly;
		_typePool = typePool;
		_declared = [..assembly.SymbolTable.DeclarationSymbols.Values];
		_bodies = genericBodies.ToDictionary(static body => body.Info.Symbol);
		
		var members = _declared.SelectMany(GetMemberFunctions).ToHashSet();
		foreach (var symbol in _declared)
		{
			switch (symbol)
			{
				case NamedTypeSymbol { Visibility: Visibility.Public }:
				case TraitSymbol { Visibility: Visibility.Public }:
				case GlobalSymbol { ContainingType: null, Visibility: Visibility.Public }:
					Reach(symbol);
					break;
				
				case FunctionSymbol { Visibility: Visibility.Public } function when !members.Contains(function):
					Reach(function);
					break;
			}
		}
		
		foreach (var conformance in typePool.Conformances.Where(IsOutsideConformance))
		{
			foreach (var witness in conformance.Witnesses.Values.OfType<FunctionWitness>())
				Reach(witness.Function);
		}
		
		foreach (var (impl, target) in typePool.MemberBlocks)
		{
			if (_declared.Contains(impl) && !_declared.Contains(target.OriginalDefinition))
				foreach (var function in GetMemberFunctions(impl).Where(IsVisibleWithType))
					Reach(function);
		}
		
		while (_pending.TryDequeue(out var next))
			Process(next);
	}
	
	private bool IsOutsideConformance(Conformance conformance) =>
		conformance.Impl is { } impl && _declared.Contains(impl) &&
		!_declared.Contains(conformance.Target.OriginalDefinition) &&
		(!_declared.Contains(conformance.Trait) || conformance.Trait.Visibility == Visibility.Public);
	
	private IEnumerable<FunctionSymbol> GetMemberFunctions(Symbol symbol) => symbol switch
	{
		RecordSymbol record => record.Members.OfType<MethodSymbol>().Select(static method => method.Function)
			.Concat(GetAccessors(record.Properties)),
		EnumSymbol enumType => enumType.Functions.Select(static method => method.Function)
			.Concat(GetAccessors(enumType.Properties)),
		TraitSymbol trait => trait.Functions.Select(static method => method.Function).Concat(trait.Constructors)
			.Concat(GetAccessors(trait.Properties)),
		ImplSymbol impl => impl.Functions.Select(static method => method.Function)
			.Concat(GetAccessors(impl.Properties)),
		_ => []
	};
	
	private static IEnumerable<FunctionSymbol> GetAccessors(IEnumerable<PropertySymbol> properties) => properties
		.SelectMany(static property => new[] { property.Getter, property.Setter })
		.OfType<FunctionAccessor>()
		.Select(static accessor => accessor.Function);
	
	private void Reach(Symbol symbol)
	{
		if (IsLocal(symbol) && _reached.Add(symbol))
			_pending.Enqueue(symbol);
	}
	
	private bool IsLocal(Symbol symbol) => symbol switch
	{
		FunctionSymbol function => _assembly.SignatureTable.Functions.ContainsKey(function) ||
		                           _bodies.ContainsKey(function),
		GlobalSymbol global => _assembly.SignatureTable.Globals.ContainsKey(global),
		_ => _declared.Contains(symbol)
	};
	
	private void Process(Symbol symbol)
	{
		switch (symbol)
		{
			case FunctionSymbol function:
				ProcessFunction(_bodies.TryGetValue(function, out var body)
					? body.Info
					: _assembly.SignatureTable.Functions[function]);
				
				break;
			
			case GlobalSymbol global:
				var info = _assembly.SignatureTable.Globals[global];
				ReachType(info.Type);
				if (info.Value is { } value)
					ReachConstant(value);
				
				break;
			
			case NamedTypeSymbol type:
				ProcessType(type);
				break;
			
			case TraitSymbol trait:
				foreach (var function in GetMemberFunctions(trait))
					Reach(function);
				
				break;
		}
	}
	
	private void ProcessFunction(FunctionInfo info)
	{
		foreach (var type in info.Signature.ParameterTypes.Append(info.Signature.ReturnType))
			ReachType(type);
		
		foreach (var constant in info.Signature.Defaults)
			ReachConstant(constant);
		
		if (!_bodies.TryGetValue(info.Symbol, out var body))
			return;
		
		foreach (var block in body.Blocks)
		{
			foreach (var instruction in block.Instructions)
			{
				switch (instruction)
				{
					case LocalVarInstruction local:
						WalkValue(local.Initializer);
						break;
					
					case ExpressionInstruction expression:
						WalkValue(expression.Value);
						break;
					
					case DropInstruction drop:
						WalkValue(drop.Value);
						break;
					
					case DropElementsInstruction drop:
						WalkValue(drop.Array);
						break;
				}
			}
			
			switch (block.Terminator)
			{
				case ReturnTerminator { Value: { } value }:
					WalkValue(value);
					break;
				
				case ConditionalBranchTerminator branch:
					WalkValue(branch.Condition);
					break;
			}
		}
	}
	
	private void ProcessType(NamedTypeSymbol type)
	{
		if (_typePool.GetDestructor(type) is { } destructor)
			Reach(destructor.Symbol);
		
		IEnumerable<FieldSymbol> fields = type switch
		{
			RecordSymbol record => _typePool.GetMembers(record).OfType<FieldSymbol>(),
			EnumSymbol enumType => _typePool.GetPayloadFields(enumType),
			_ => []
		};
		
		foreach (var field in fields)
			ReachType(_typePool.GetTypeOfMember(field));
		
		foreach (var function in GetMemberFunctions(type).Where(IsVisibleWithType))
			Reach(function);
		
		foreach (var constructor in _typePool.GetConstructors(type).Where(info => IsVisibleWithType(info.Symbol)))
			Reach(constructor.Symbol);
		
		foreach (var global in type.StaticFields.Where(static global => IsVisibleWithType(global.Visibility)))
			Reach(global);
		
		foreach (var witness in _typePool.GetWitnessFunctions(type))
			Reach(witness);
		
		foreach (var function in _typePool.GetMemberBlocks(type).SelectMany(GetMemberFunctions)
			         .Where(IsVisibleWithType))
			Reach(function);
	}
	
	private static bool IsVisibleWithType(FunctionSymbol function) =>
		function.Kind == FunctionKind.Destructor || IsVisibleWithType(function.Visibility);
	
	private static bool IsVisibleWithType(Visibility visibility) => visibility >= Visibility.Project;
	
	private void ReachFunction(FunctionInfo info)
	{
		if (!CodeGenerator.IsActive(info.Symbol))
			return;
		
		Reach(info.Symbol);
		if (!info.TypeArguments.IsDefaultOrEmpty)
			foreach (var argument in info.TypeArguments)
				ReachType(argument);
	}
	
	private void ReachGlobal(GlobalSymbol global)
	{
		if (global.ContainingType is NamedTypeSymbol { IsGenericInstance: true } instance &&
		    instance.Definition.GetStaticField(global.Name) is { } definition)
		{
			Reach(definition);
			ReachType(instance);
			return;
		}
		
		Reach(global);
	}
	
	private void ReachType(TypeSymbol type)
	{
		if (!_visitedTypes.Add(type))
			return;
		
		switch (type)
		{
			case NamedTypeSymbol named:
				Reach(named.Definition);
				foreach (var argument in named.TypeArguments)
					ReachType(argument);
				
				break;
			
			case PointerType pointer:
				ReachType(pointer.BaseType);
				break;
			
			case BorrowType borrow:
				ReachType(borrow.Target);
				break;
			
			case ArrayType array:
				ReachType(array.ElementType);
				break;
			
			case FunctionType function:
				foreach (var part in function.ParameterTypes.Append(function.ReturnType))
					ReachType(part);
				
				break;
			
			case ClosureType closure:
				ReachFunction(closure.Function);
				foreach (var capture in closure.CaptureTypes)
					ReachType(capture);
				
				break;
			
			case DynType { Instance: { } instance }:
				ReachType(instance);
				break;
			
			case TraitType trait:
				Reach(trait.Trait);
				foreach (var argument in trait.Arguments)
					ReachType(argument);
				
				break;
			
			case FStrType fstr:
				ReachType(fstr.Value);
				break;
		}
	}
	
	private void ReachConstant(Constant constant)
	{
		ReachType(constant.Type);
		switch (constant)
		{
			case FunctionConstant function:
				ReachFunction(function.Function);
				break;
			
			case RecordConstant record:
				foreach (var field in record.Fields)
					ReachConstant(field);
				
				break;
			
			case ArrayConstant array:
				foreach (var element in array.Elements)
					ReachConstant(element);
				
				break;
			
			case EnumConstant enumConstant:
				foreach (var value in enumConstant.Payload)
					ReachConstant(value);
				
				break;
		}
	}
	
	private void WalkValue(Value? value)
	{
		if (value is null)
			return;
		
		ReachType(value.Type);
		switch (value)
		{
			case SizeOfValue size:
				ReachType(size.Target);
				break;
			
			case AlignOfValue align:
				ReachType(align.Target);
				break;
			
			case GlobalValue global:
				ReachGlobal(global.Global.Symbol);
				break;
			
			case MoveValue move:
				WalkValue(move.Place);
				break;
			
			case ConversionValue conversion:
				WalkConversion(conversion.Conversion);
				WalkValue(conversion.Source);
				break;
			
			case CallValue call:
				ReachFunction(call.Function);
				WalkValues(call.Arguments);
				break;
			
			case FunctionReferenceValue reference:
				ReachFunction(reference.Function);
				break;
			
			case ClosureValue closure:
				ReachFunction(closure.Function);
				WalkValues(closure.Captures);
				break;
			
			case OwnClosureValue closure:
				ReachFunction(closure.Function);
				WalkValues(closure.Captures);
				break;
			
			case IndirectCallValue call:
				WalkValue(call.Target);
				WalkValues(call.Arguments);
				break;
			
			case IndexerValue indexer:
				WalkValue(indexer.Target);
				WalkValue(indexer.Index);
				break;
			
			case AccessValue access:
				WalkValue(access.Target);
				break;
			
			case EnumValue enumValue:
				WalkValues(enumValue.Payload);
				break;
			
			case EnumTagValue tag:
				WalkValue(tag.Target);
				break;
			
			case EnumPayloadValue payload:
				WalkValue(payload.Target);
				break;
			
			case ArrayValue array:
				WalkValues(array.Elements);
				break;
			
			case FStrValue fstr:
				WalkValue(fstr.Text);
				WalkValue(fstr.Values);
				break;
			
			case AtomicValue atomic:
				WalkValue(atomic.Pointer);
				WalkValue(atomic.Expected);
				WalkValue(atomic.Operand);
				break;
			
			case FStrPartValue part:
				WalkValue(part.Target);
				WalkValue(part.Index);
				break;
			
			case UnaryOpValue unary:
				WalkValue(unary.Operand);
				break;
			
			case BinOpValue binary:
				WalkValue(binary.Left);
				WalkValue(binary.Right);
				break;
			
			case AssignValue assign:
				WalkValue(assign.Left);
				WalkValue(assign.Right);
				break;
			
			case PointerOffsetValue offset:
				WalkValue(offset.Pointer);
				WalkValue(offset.Offset);
				break;
			
			case PointerDifferenceValue difference:
				WalkValue(difference.Left);
				WalkValue(difference.Right);
				break;
		}
	}
	
	private void WalkValues(IEnumerable<Value> values)
	{
		foreach (var value in values)
			WalkValue(value);
	}
	
	private void WalkConversion(Conversion conversion)
	{
		switch (conversion)
		{
			case FunctionConversion function:
				ReachFunction(function.Function);
				break;
			
			case DynConversion dyn:
				ReachType(dyn.ObjectType);
				foreach (var member in dyn.Members)
					ReachFunction(member);
				
				break;
		}
	}
}