using System.Collections.Immutable;
using System.Text;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding.Constants;

public sealed partial class ConstantEvaluator
{
	private const int StepLimit = 1_000_000;
	private const int DepthLimit = 200;
	
	private static readonly Constant _void = new ZeroConstant(NativeSymbols.Void);
	
	private readonly Stack<CallFrame> _frames = [];
	private readonly HashSet<(SourceLocation, string)> _reportedFaults = [];
	private readonly HashSet<FunctionSymbol> _foldingFunctions = [];
	private readonly HashSet<FunctionSymbol> _functionValues = [];
	private bool foldsCalls;
	private int steps;
	
	public Func<FunctionSymbol, ConstantBody?>? ResolveBody { get; set; }
	public Action<Diagnostic>? ReportFault { get; init; }
	
	public IReadOnlySet<FunctionSymbol> FunctionValues => _functionValues;
	
	private bool IsInterpreting => _frames.Count > 0;
	
	private FunctionConstant ReferenceFunction(ResolvedFunctionReferenceExpressionNode node)
	{
		_functionValues.Add(node.Function.Symbol);
		return new FunctionConstant(node.Function, node.Type);
	}
	
	public Constant? EvaluateRequired(IResolvedExpressionNode node)
	{
		var outer = foldsCalls;
		foldsCalls = true;
		try
		{
			return Settle(Fold(node));
		}
		finally
		{
			foldsCalls = outer;
		}
	}
	
	public Constant? FoldFunction(FunctionSymbol function)
	{
		if (typePool.TryGetFoldedFunction(function, out var folded))
			return folded;
		
		if (!_foldingFunctions.Add(function))
			return null;
		
		try
		{
			var value = ResolveBody?.Invoke(function) is { } body
				? Settle(InvokeBody(body.Node.FunctionInfo, [], function.Definition, true))
				: null;
			
			typePool.RegisterFoldedFunction(function, value);
			return value;
		}
		finally
		{
			_foldingFunctions.Remove(function);
		}
	}
	
	private static Constant? Settle(Constant? value) => value is not null && HoldsReference(value) ? null : value;
	
	private static bool HoldsReference(Constant value) => value switch
	{
		ReferenceConstant => true,
		RecordConstant record => record.Fields.Any(HoldsReference),
		ArrayConstant array => array.Elements.Any(HoldsReference),
		EnumConstant enumValue => enumValue.Payload.Any(HoldsReference),
		_ => false
	};
	
	private FunctionInfo SubstituteFunction(FunctionInfo function) =>
		substitution is null ? function : typePool.SubstituteFunction(function, substitution);
	
	private TypeSymbol SubstituteType(TypeSymbol type) =>
		substitution is null ? type : typePool.Substitute(type, substitution);
	
	private static bool IsConcrete(FunctionInfo function) =>
		function.TypeArguments.IsDefaultOrEmpty || !function.TypeArguments.Any(TypePool.ContainsTypeParameters);
	
	private Constant? FoldCall(ResolvedFunctionCallExpressionNode node)
	{
		if (node.Syntax is PanicExpressionNode && IsInterpreting)
			throw new EvaluationFault(FaultKind.Panic,
				node.Arguments is [var message, _] ? DescribeMessage(message) ?? "" : "", node.Syntax.SourceLocation)
			{
				Function = _frames.Peek().Function.Symbol.Name
			};
		
		var function = SubstituteFunction(node.Function);
		if (TypePool.FoldsAsConstant(function.Symbol))
			return FoldFunction(function.Symbol);
		
		if (!foldsCalls && !IsInterpreting || !IsConcrete(function))
			return null;
		
		return EvaluateArguments(node.Arguments, function, 0) is { } arguments
			? Invoke(function, arguments, node.Syntax.SourceLocation)
			: null;
	}
	
	private Constant? FoldConstruction(ResolvedConstructorCallExpressionNode node)
	{
		var function = SubstituteFunction(node.Function);
		if (!foldsCalls && !IsInterpreting || !IsConcrete(function))
			return null;
		
		return EvaluateArguments(node.Arguments, function, 1) is { } arguments
			? Construct(function, SubstituteType(node.Type), arguments, node.Syntax.SourceLocation)
			: null;
	}
	
	private Constant? FoldNew(ResolvedNewExpressionNode node)
	{
		var type = SubstituteType(node.Type);
		if (TypePool.ContainsTypeParameters(type))
			return null;
		
		if (typePool.FindNewConstructor(type) is not { } constructor)
			return Zero(type);
		
		if (!foldsCalls && !IsInterpreting)
			return null;
		
		return EvaluateArguments([], constructor, 1) is { } arguments
			? Construct(constructor, type, arguments, node.Syntax.SourceLocation)
			: null;
	}
	
	private string? DescribeMessage(IResolvedExpressionNode message)
	{
		try
		{
			return message switch
			{
				ResolvedFStrExpressionNode { Text: { } text } => DescribeMessage(text),
				ResolvedFStrExpressionNode template => DescribeTemplate(template),
				ResolvedConversionExpressionNode conversion => DescribeMessage(conversion.Source),
				_ => DescribePart(Evaluate(message))
			};
		}
		catch (Exception exception) when (exception is EvaluationFault or AbortedEvaluation or JumpSignal)
		{
			return null;
		}
	}
	
	private string? DescribeTemplate(ResolvedFStrExpressionNode template)
	{
		var text = new StringBuilder(template.Texts[0]);
		for (var i = 0; i < template.Values.Length; i++)
		{
			if (template.Texts[2 * i + 1].Length > 0)
				return null;
			
			var value = template.Values[i] is ResolvedConversionExpressionNode { Conversion: DynConversion } dyn
				? dyn.Source
				: template.Values[i];
			
			if (DescribePart(Evaluate(value)) is not { } part)
				return null;
			
			text.Append(part).Append(template.Texts[2 * i + 2]);
		}
		
		return text.ToString();
	}
	
	private static string? DescribePart(Constant? value) => value switch
	{
		StringConstant text => text.Text,
		IntegerConstant { Type: CharType } character => char.ConvertFromUtf32((int)character.Value),
		IntegerConstant integer => integer.Value.ToString(),
		BoolConstant flag => flag.Value ? "true" : "false",
		FloatConstant number => ConstantText.Describe(number),
		_ => null
	};
	
	private Constant[]? EvaluateArguments(ImmutableArray<IResolvedExpressionNode> arguments, FunctionInfo function,
		int firstParameter)
	{
		var signature = function.DeclaredSignature;
		if (signature.HasVariadicParameter || firstParameter + arguments.Length > signature.FixedCount)
			return null;
		
		var values = new Constant[signature.FixedCount - firstParameter];
		var firstDefault = signature.FixedCount - signature.Defaults.Length;
		for (var i = 0; i < values.Length; i++)
		{
			var index = firstParameter + i;
			var value = i >= arguments.Length
				? index >= firstDefault ? FoldStored(signature.Defaults[index - firstDefault]) : null
				: TypePool.PassesByPointer(signature.GetMode(index))
					? Reference(arguments[i])
					: Evaluate(arguments[i]);
			
			if (value is null)
				return null;
			
			values[i] = value;
		}
		
		return values;
	}
	
	private Constant? Invoke(FunctionInfo function, Constant[] arguments, SourceLocation callSite) =>
		function.Symbol.Trait is not null
			? InvokeRequirement(function, arguments, callSite)
			: InvokeBody(function, arguments, callSite, false);
	
	private Constant? InvokeBody(FunctionInfo function, Constant[] arguments, SourceLocation callSite, bool isRoot)
	{
		if (arguments.Any(static argument => argument is InvalidConstant))
			return InvalidConstant.Instance;
		
		if (ResolveBody?.Invoke(function.Symbol) is not { } body)
			return null;
		
		if (!body.IsValid || body.Node.Body is not IResolvedStatementNode statement)
			return InvalidConstant.Instance;
		
		var parameters = function.Symbol.Parameters;
		if (parameters.Length != arguments.Length)
			return null;
		
		var isOutermost = isRoot || !IsInterpreting;
		try
		{
			if (_frames.Count >= DepthLimit)
				throw new EvaluationFault(FaultKind.Depth, "", callSite);
			
			var frame = new CallFrame(function, function.TypeArguments.IsDefaultOrEmpty
				? null
				: TypePool.CreateMap(function.Symbol.TypeParameters, function.TypeArguments));
			
			var signature = function.DeclaredSignature;
			for (var i = 0; i < parameters.Length; i++)
				frame.Locals[parameters[i]] = new Cell(Pass(arguments[i], signature.ParameterTypes[i],
					signature.GetMode(i)));
			
			var saved = Enter(frame, isOutermost);
			try
			{
				var flow = Execute(statement);
				return flow.Kind == FlowKind.Return ? flow.Value ?? _void : _void;
			}
			finally
			{
				Leave(saved);
			}
		}
		catch (EvaluationFault fault) when (isOutermost)
		{
			Report(fault, function, callSite);
			return InvalidConstant.Instance;
		}
		catch (AbortedEvaluation) when (isOutermost)
		{
			return InvalidConstant.Instance;
		}
	}
	
	private Constant Pass(Constant argument, TypeSymbol type, ParameterMode mode)
	{
		if (TypePool.PassesByPointer(mode) || mode == ParameterMode.Mut)
			return argument is ReferenceConstant ? argument : new ReferenceConstant(type, Temporary(argument));
		
		return argument is ReferenceConstant reference && type is not (BorrowType or PointerType)
			? Read(reference.Place)
			: argument;
	}
	
	private Constant? InvokeRequirement(FunctionInfo function, Constant[] arguments, SourceLocation callSite)
	{
		if (function.TypeArguments.IsDefaultOrEmpty || TypePool.ContainsTypeParameters(function.TypeArguments[0]))
			return null;
		
		var self = function.TypeArguments[0];
		var traitArguments = TypePool.GetTraitArguments(function);
		var trait = typePool.GetTraitType(function.Symbol.Trait!, traitArguments);
		if (!function.Symbol.IsConstant && !typePool.IsConstantConformance(self, trait))
			return IsInterpreting
				? throw new EvaluationFault(FaultKind.Unsupported, "", callSite)
				{
					Function = _frames.Peek().Function.Symbol.Name,
					Detail = $"'{self.Name}' doesn't implement 'const {trait.Name}'"
				}
				: null;
		
		var witness = typePool.FindWitness(self, function.Symbol, traitArguments);
		if (witness is null or FunctionWitness { Function.Trait: not null } &&
		    typePool.FindNativeWitness(function.Symbol, function.Signature, self) is { } native)
			witness = native;
		
		switch (witness)
		{
			case FunctionWitness { Function: var target } when target == function.Symbol:
				return target.IsConstant ||
				       typePool.IsConstantConformance(self, typePool.GetTraitType(target.Trait!, traitArguments))
					? InvokeBody(function, arguments, callSite, false)
					: null;
			
			case FunctionWitness target:
				var declared = function.TypeArguments.TakeLast(function.Symbol.DeclaredTypeParameters.Length);
				var typeArguments = typePool.GetWitnessArguments(self, target.Function, traitArguments, declared,
					function.Symbol.Trait);
				
				var instance = typePool.InstantiateFunction(target.Info, typeArguments);
				if (target.Function.Trait is not { } owner)
					return Invoke(instance, arguments, callSite);
				
				var ownerArguments = typeArguments.Skip(1).Take(owner.TypeParameters.Length);
				return target.Function.IsConstant ||
				       typePool.IsConstantConformance(self, typePool.GetTraitType(owner, [..ownerArguments]))
					? InvokeBody(instance, arguments, callSite, false)
					: null;
			
			case NativeWitness nativeWitness:
				return ApplyNative(nativeWitness, function.Signature.ReturnType, arguments);
			
			case ConversionWitness { Conversion: var conversion } when arguments is [var source]:
				return Convert(Dereference(source), conversion);
			
			case SelfConversionWitness { Conversion: var conversion } when arguments is [var source]:
				return Convert(Dereference(source), conversion);
			
			default:
				return null;
		}
	}
	
	private Constant? ApplyNative(NativeWitness witness, TypeSymbol returnType, Constant[] arguments)
	{
		var operation = witness.Operation;
		if (witness.IsCompound)
		{
			if (arguments is not [ReferenceConstant target, var operand] ||
			    Apply(operation, Read(target.Place), Dereference(operand), operation.ReturnType) is not { } result)
				return null;
			
			Write(target.Place, result);
			return _void;
		}
		
		var values = arguments.Select(Dereference).ToArray();
		return values switch
		{
			[var left, var right] => Apply(operation, left, right, returnType),
			[var operand] => ApplyUnary(operation, operand, returnType),
			_ => null
		};
	}
	
	private Constant? ApplyUnary(OperationImpl operation, Constant operand, TypeSymbol type)
	{
		if (operand.Type is EnumSymbol enumType && GetTag(operand) is { } tag)
			operand = new IntegerConstant(typePool.GetTagType(enumType), tag);
		
		return (OperationMapping.ToUnaryOperation(operation.Op), operand) switch
		{
			(UnaryOperation.Identity, var value) => value,
			(UnaryOperation.Negation, IntegerConstant value) => Checked(type, -value.Value),
			(UnaryOperation.Negation, FloatConstant value) => Float(type, -value.Value),
			(UnaryOperation.BitwiseNot, IntegerConstant value) => Integer(type, ~value.Value),
			(UnaryOperation.LogicalNot, BoolConstant value) => BoolConstant.From(!value.Value),
			_ => null
		};
	}
	
	private Constant? Construct(FunctionInfo function, TypeSymbol type, Constant[] arguments, SourceLocation callSite)
	{
		if (function.Symbol.Trait is not null || function.Symbol.Syntax is ConstructorConstraintNode)
		{
			var witness = function.Symbol.Trait is null
				? typePool.FindConstructorWitness(type, function.Signature)
				: typePool.FindWitness(type, function.Symbol, TypePool.GetTraitArguments(function));
			
			return witness switch
			{
				MemberwiseWitness => new RecordConstant(type, arguments.Select(Dereference)),
				DefaultWitness => Zero(type),
				ConversionWitness { Conversion: var conversion } when arguments is [var source] =>
					Convert(Dereference(source), conversion),
				FunctionWitness target => Construct(type.TypeArguments.IsEmpty
					? target.Info
					: typePool.InstantiateFunction(target.Info, type.TypeArguments), type, arguments, callSite),
				_ => null
			};
		}
		
		if (typePool.IsDefaultConstructor(function))
			return Zero(type);
		
		var self = new Cell(Zero(type));
		var result = InvokeBody(function,
			[new ReferenceConstant(function.Signature.ParameterTypes[0], new Place(self, [])), ..arguments], callSite,
			false);
		
		return result is null or InvalidConstant ? result : self.Value;
	}
	
	private void Report(EvaluationFault fault, FunctionInfo function, SourceLocation callSite)
	{
		var name = fault.Function ?? function.Symbol.Name;
		var message = fault.Kind switch
		{
			FaultKind.Steps => $"Evaluating '{function.Symbol.Name}' takes too many steps",
			FaultKind.Depth => $"Evaluating '{function.Symbol.Name}' recurses too deeply",
			FaultKind.Unsupported => $"Cannot evaluate '{name}' at compile time",
			_ when fault.Message.Length == 0 => $"Panic in '{name}'",
			_ => $"Panic in '{name}': {fault.Message}"
		};
		
		if (fault.IsChecked || !_reportedFaults.Add((callSite, message)))
			return;
		
		var hints = fault.Detail is { } detail
			? [detail]
			: fault.Kind is FaultKind.Steps or FaultKind.Depth || fault.Location == SourceLocation.None
				? ImmutableArray<string>.Empty
				: [DescribeFaultLocation(fault.Location, callSite)];
		
		ReportFault?.Invoke(new Diagnostic(DiagnosticSeverity.Error, callSite, message) { Hints = hints });
	}
	
	private static string DescribeFaultLocation(SourceLocation location, SourceLocation callSite)
	{
		var (line, _) = location.GetLineColumn();
		return location.Source == callSite.Source ? $"On line {line}" : $"In '{location.Source.Name}' on line {line}";
	}
	
	private EvaluationFault Fault(IResolvedExpressionNode node)
	{
		var message = DescribeFailure(node);
		return new EvaluationFault(message is null ? FaultKind.Unsupported : FaultKind.Panic, message ?? "",
			node.Syntax.SourceLocation)
		{
			Function = _frames.TryPeek(out var frame) ? frame.Function.Symbol.Name : null,
			IsChecked = IsStatic(node)
		};
	}
	
	private static bool IsStatic(IResolvedExpressionNode node) => node switch
	{
		ResolvedLiteralExpressionNode => true,
		ResolvedGlobalExpressionNode { Symbol.IsConstant: true } => true,
		ResolvedVarExpressionNode { Symbol: LocalVariableSymbol { ConstantValue: not (null or ParametricConstant) } } =>
			true,
		ResolvedFunctionCallExpressionNode { Arguments.IsEmpty: true } call =>
			TypePool.FoldsAsConstant(call.Function.Symbol),
		ResolvedUnaryOpExpressionNode or ResolvedBinaryOpExpressionNode or ResolvedConversionExpressionNode =>
			GetOperands(node).All(IsStatic),
		_ => false
	};
	
	private string? DescribeFailure(IResolvedExpressionNode node) => node switch
	{
		ResolvedTrapExpressionNode => "",
		ResolvedIndexerExpressionNode => "index out of bounds",
		ResolvedConversionExpressionNode { Conversion: EnumConversion conversion }
			when SubstituteType(conversion.To) is EnumSymbol { IsExternal: false } target =>
			$"'{target.Name}' has no case with this value",
		ResolvedBinaryOpExpressionNode or ResolvedUnaryOpExpressionNode { Operation: NativeImpl } or
			ResolvedConversionExpressionNode => DescribeFault(node),
		ResolvedAssignmentExpressionNode { Operation: NativeImpl native } => DescribeCompound(native),
		_ => null
	};
	
	private static string DescribeCompound(NativeImpl operation) =>
		OperationMapping.ToBinaryOperation(operation.Op) switch
		{
			BinaryOperation.Addition => "addition overflow",
			BinaryOperation.Subtraction => "subtraction overflow",
			BinaryOperation.Multiplication => "multiplication overflow",
			BinaryOperation.Division or BinaryOperation.Modulo => "division overflow",
			_ => "constant overflow"
		};
	
	private void Step()
	{
		if (++steps > StepLimit)
			throw new EvaluationFault(FaultKind.Steps, "", SourceLocation.None);
	}
	
	private SavedState Enter(CallFrame frame, bool resetsSteps)
	{
		var saved = new SavedState(substitution, foldsBindings, steps, resetsSteps,
			new Dictionary<LocalVariableSymbol, Constant>(_bindings), [.._patternBindings]);
		
		_bindings.Clear();
		_patternBindings.Clear();
		_frames.Push(frame);
		substitution = frame.Substitution;
		foldsBindings = true;
		if (resetsSteps)
			steps = 0;
		
		return saved;
	}
	
	private void Leave(SavedState saved)
	{
		_frames.Pop();
		substitution = saved.Substitution;
		foldsBindings = saved.FoldsBindings;
		if (saved.ResetsSteps)
			steps = saved.Steps;
		
		_bindings.Clear();
		foreach (var (binding, value) in saved.Bindings)
			_bindings[binding] = value;
		
		_patternBindings.Clear();
		_patternBindings.AddRange(saved.PatternBindings);
	}
	
	private sealed record SavedState
	(
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? Substitution,
		bool FoldsBindings,
		int Steps,
		bool ResetsSteps,
		Dictionary<LocalVariableSymbol, Constant> Bindings,
		List<LocalVariableSymbol> PatternBindings
	);
	
	private sealed class CallFrame(FunctionInfo function, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		public FunctionInfo Function { get; } = function;
		public IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? Substitution { get; } = map;
		public Dictionary<VariableSymbol, Cell> Locals { get; } = [];
	}
	
	private sealed class Cell(Constant? value)
	{
		public Constant? Value { get; set; } = value;
	}
	
	private sealed record Place(Cell Root, ImmutableArray<PlaceStep> Path)
	{
		public Place Then(PlaceStep step) => this with { Path = Path.Add(step) };
	}
	
	private abstract record PlaceStep;
	
	private sealed record FieldStep(FieldSymbol Field) : PlaceStep;
	
	private sealed record ElementStep(int Position) : PlaceStep;
	
	private sealed record PayloadStep(EnumCaseSymbol Case, int Position) : PlaceStep;
	
	private sealed class ReferenceConstant(TypeSymbol type, Place place) : Constant(type)
	{
		public Place Place { get; } = place;
	}
	
	private enum FaultKind
	{
		Panic,
		Unsupported,
		Steps,
		Depth
	}
	
	private sealed class EvaluationFault(FaultKind kind, string message, SourceLocation location) : Exception(message)
	{
		public FaultKind Kind { get; } = kind;
		public SourceLocation Location { get; } = location;
		public string? Function { get; init; }
		public bool IsChecked { get; init; }
		public string? Detail { get; init; }
	}
	
	private sealed class AbortedEvaluation : Exception;
}