using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	public IResolvedExpressionNode Visit(LambdaExpressionNode node) => ResolveLambda(node, ExpectedType);
	
	private IResolvedExpressionNode ResolveLambda(LambdaExpressionNode node, TypeSymbol? expected,
		bool resolveBody = true, bool isArgument = false)
	{
		var outer = CurrentResolutionContext;
		var target = (expected is BorrowType { IsMutable: false } borrow ? borrow.Target : expected) as FunctionType;
		if (target is not null && target.ParameterTypes.Length != node.Parameters.Length)
			return Error(node, $"'{(isArgument ? target.PlainName : target.Name)}' takes " +
			                   DescribeParameterCount(target.ParameterTypes.Length), expected);
		
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(node.Parameters.Select(static p => p.Identifier),
			static name => $"Parameter '{name}' is declared more than once"));
		
		var modes = new ParameterMode[node.Parameters.Length];
		var types = new TypeSymbol[node.Parameters.Length];
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var parameter = node.Parameters[i];
			modes[i] = parameter.Mode is null && target is not null
				? target.ParameterModes[i]
				: SymbolCollector.GetMode(parameter.Mode);
			
			if (parameter.Type is BorrowTypeNode borrowType)
				Diagnostics.Add(DiagnosticReporter.ReportBorrowParameter(borrowType, parameter.Identifier.Text,
					parameter.Mode is not null, false));
			else if (parameter.Type is FunctionTypeNode { IsRef: true } refType)
				Diagnostics.Add(DiagnosticReporter.ReportRefFunctionParameter(refType, parameter.Identifier.Text,
					parameter.Mode is not null, false));
			
			if (parameter.Type is { } typeNode)
			{
				types[i] = _typePool.GetPassedType(outer.ResolveType(typeNode), modes[i]);
				if (modes[i] == ParameterMode.ReadOnly)
					_copiedParameters.Add(types[i], parameter.Identifier.SourceLocation);
			}
			else if (target is not null && IsKnown(target.ParameterTypes[i]))
				types[i] = GetExpectedParameterType(target, i, modes[i]);
			else
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, parameter.Identifier.SourceLocation,
					$"Cannot infer the type of '{parameter.Identifier.Text}'"));
				
				types[i] = NativeSymbols.Invalid;
			}
		}
		
		var agrees = target is not null && types.Select((type, i) => modes[i] == target.ParameterModes[i] &&
		                                                             (type == target.ParameterTypes[i] ||
		                                                              !IsKnown(target.ParameterTypes[i])))
			.All(static agrees => agrees);
		
		var takesVariadic = target is { HasVariadicParameter: true } && types[^1] == target.ParameterTypes[^1];
		var returnType = node.ReturnType is { } returnNode
			? outer.ResolveReturnType(returnNode)
			: !agrees
				? NativeSymbols.Void
				: IsKnown(target!.ReturnType)
					? target.ReturnType
					: null;
		
		if (returnType is null)
		{
			if (node.ExpressionBody is not { } probe)
				return Error(node, "Cannot infer the return type", null);
			
			var checkpoint = OpenCheckpoint();
			EnterLambda(outer, CreateLambdaInfo(node, outer, modes, types, NativeSymbols.Void, takesVariadic), target);
			var value = Decay(MaterializeAsDefault(VisitNode(probe, null)));
			ExitLambda();
			Rollback(checkpoint);
			returnType = value.Type;
		}
		
		var info = CreateLambdaInfo(node, outer, modes, types, returnType, takesVariadic);
		var isInvalid = returnType is InvalidType || types.Any(static type => type is InvalidType);
		if (!resolveBody)
			return isInvalid ? new ResolvedInvalidExpressionNode(node, expected) : CreateLambdaGroup(info, node);
		
		var frame = EnterLambda(outer, info, target);
		IResolvedNode body = node.ExpressionBody is { } expression
			? VisitNode(new ExpressionStatementNode(returnType == NativeSymbols.Void
				? expression
				: new ReturnExpressionNode(expression.SourceLocation, expression)))
			: VisitNode(node.BlockBody!);
		
		ExitLambda();
		var function = new ResolvedFunctionNode(info, body, info.Symbol.Syntax);
		_lambdas.Add(function);
		Journal(() => _lambdas.Remove(function));
		if (isInvalid)
			return new ResolvedInvalidExpressionNode(node, expected);
		
		if (frame.Captures.Count == 0)
		{
			if (node.Own is { } own && !frame.RejectsCaptures)
				Diagnostics.Add(new(DiagnosticSeverity.Hint, own.SourceLocation, "Redundant 'own'"));
			
			return CreateLambdaGroup(info, node);
		}
		
		List<(LocalVariableSymbol Binding, VarExpressionNode Use)> captures = node.Own is null
			? frame.Captures
			: [..frame.Captures.OrderBy(static capture => GetDeclarationStart(capture.Binding))];
		
		info.Symbol.Captures = [..captures.Select(static capture => capture.Binding)];
		info.Symbol.OwnsCaptures = node.Own is not null;
		var signature = _typePool.GetFunctionType(false, types, modes, returnType, node.Own is null, false,
			takesVariadic);
		
		var type = node.Own is null
			? signature
			: (TypeSymbol)_typePool.GetClosureType(info,
				[..captures.Select(static capture => ((PointerType)capture.Binding.Type).BaseType)], signature,
				info.MangledName!);
		
		return new ResolvedClosureExpressionNode(info,
			[..captures.Select(capture => ResolveSymbolValue(capture.Use, capture.Binding.Captured!))], type, node);
	}
	
	private static int GetDeclarationStart(VariableSymbol variable) => variable switch
	{
		LocalVariableSymbol { Captured: { } captured } => GetDeclarationStart(captured),
		LocalVariableSymbol local => local.Identifier.SourceLocation.Range.Start,
		ParameterSymbol parameter => parameter.Definition.Range.Start,
		_ => 0
	};
	
	private ResolvedFunctionGroupExpressionNode CreateLambdaGroup(FunctionInfo info, LambdaExpressionNode node) =>
		new(new FunctionGroupType("fun", [info], GetNaturalType(info).Name), node);
	
	private TypeSymbol GetExpectedParameterType(FunctionType target, int index, ParameterMode mode)
	{
		var type = target.ParameterTypes[index];
		if (mode == target.ParameterModes[index])
			return type;
		
		return _typePool.GetPassedType(target.ParameterModes[index] == ParameterMode.Mut && type is PointerType pointer
			? pointer.BaseType
			: type, mode);
	}
	
	private bool ReportSingleMismatch(CallExpressionNode node, string name, ICallable original, ICallable? callable,
		IResolvedExpressionNode[] args, SourceLocation location)
	{
		if (FindSpread(node.Arguments) >= 0)
			return false;
		
		var hasRest = original.IsVariadic || original.HasVariadicParameter;
		var fixedCount = original.ParameterTypes.Length - (original.HasVariadicParameter ? 1 : 0);
		var minimum = fixedCount - (GetInfo(original)?.Signature.Defaults.Length ?? 0);
		if (args.Length < minimum || !hasRest && args.Length > fixedCount)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"'{name}' takes {DescribeArgumentCount(minimum, hasRest ? null : fixedCount)}"));
			
			return true;
		}
		
		if (callable is null)
			return false;
		
		if (CurrentTargetType is { } target &&
		    ResolveCallable([callable], args, MaterializationMode.Overload).HasResult)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"Cannot convert type '{callable.ReturnType.Name}' to '{target.Name}'"));
			
			return true;
		}
		
		for (var i = 0; i < Math.Min(args.Length, callable.ParameterTypes.Length); i++)
		{
			var parameterType = callable.ParameterTypes[i];
			if (MatchArg(args[i], parameterType, callable.GetMode(i), MaterializationMode.Overload, false).Cost !=
			    int.MaxValue)
				continue;
			
			return CoerceToType(args[i], parameterType) is ResolvedInvalidExpressionNode;
		}
		
		return false;
	}
	
	private static string DescribeArgumentCount(int minimum, int? maximum) => maximum switch
	{
		null => minimum == 1 ? "at least one argument" : $"at least {minimum} arguments",
		_ when maximum != minimum => $"{minimum} to {maximum} arguments",
		_ => minimum switch
		{
			0 => "no arguments",
			1 => "one argument",
			_ => $"{minimum} arguments"
		}
	};
	
	private static string DescribeParameterCount(int count) => count switch
	{
		0 => "no parameters",
		1 => "one parameter",
		_ => $"{count} parameters"
	};
	
	private bool IsKnown(TypeSymbol type) => TypePool.FindTypeParameters(type)
		.All(parameter => CurrentResolutionContext.Resolve(parameter.Name) == parameter);
	
	private FunctionInfo CreateLambdaInfo(LambdaExpressionNode node, ResolutionContext outer,
		IReadOnlyList<ParameterMode> modes, IReadOnlyList<TypeSymbol> types, TypeSymbol returnType, bool takesVariadic)
	{
		var parameters = node.Parameters
			.Select((parameter, i) => new ParameterSymbol(parameter.Identifier) { Mode = modes[i] })
			.ToList();
		
		var symbol = new FunctionSymbol($"lambda{++lambdaCount}", new LambdaDeclarationNode(node), Visibility.Private,
			outer.ContainingFunction, parameters, FunctionKind.Free)
		{
			TypeParameters = outer.ContainingFunction?.Symbol.TypeParameters ?? GetEnclosingTypeParameters(outer)
		};
		
		var scope = new Scope();
		foreach (var parameter in parameters)
			scope.Define(parameter);
		
		var signature = new FunctionSignature(types, returnType, false, modes, takesVariadic);
		symbol.QualifiedName = outer.Qualify(symbol);
		var (mangledName, mangledBase) = outer.Mangle(symbol, signature);
		var info = new FunctionInfo(mangledName, symbol, signature, scope, null, outer.File)
			{ MangledBase = mangledBase };
		
		_signatures.AddLambda(info);
		Journal(() => _signatures.RemoveLambda(info));
		return info;
	}
	
	private static ImmutableArray<TypeParameterSymbol> GetEnclosingTypeParameters(ResolutionContext context) =>
	[
		..context.ContainingType is NamedTypeSymbol { IsGenericDefinition: true } type ? type.TypeParameters : [],
		..context.TypeParameters.IsDefault ? [] : context.TypeParameters
	];
	
	private LambdaFrame EnterLambda(ResolutionContext outer, FunctionInfo info, FunctionType? target)
	{
		var frame = new LambdaFrame(outer, target);
		_lambdaFrames.Add(frame);
		_resolutionContexts.Push(outer with
		{
			ContainingFunction = info,
			LocalScope = info.Scope,
			Capture = name => (Symbol?)frame.Bindings.GetValueOrDefault(name) ??
			                  (outer.ResolveNear(name) is LocalVariableSymbol or ParameterSymbol or CapturedSymbol
				                  ? new CapturedSymbol(name)
				                  : null)
		});
		
		return frame;
	}
	
	private void ExitLambda()
	{
		_resolutionContexts.Pop();
		_lambdaFrames.RemoveAt(_lambdaFrames.Count - 1);
	}
	
	private IResolvedExpressionNode ResolveCapture(VarExpressionNode node)
	{
		if (survey is { } activeSurvey &&
		    FindCapturedLocal(_lambdaFrames.Count - 1, node.Identifier.Text) is { } local &&
		    activeSurvey.Find(local) is { } open)
			return node == storeTarget || node == placeTarget
				? new ResolvedVarExpressionNode(local, local.Type, node)
				: Substitute(open);
		
		return Capture(_lambdaFrames.Count - 1, node) is { } binding
			? ResolveSymbolValue(node, binding)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private LocalVariableSymbol? FindCapturedLocal(int level, string name) =>
		_lambdaFrames[level].Outer.ResolveNear(name) switch
		{
			CapturedSymbol when level > 0 => FindCapturedLocal(level - 1, name),
			var outer => outer as LocalVariableSymbol
		};
	
	private LocalVariableSymbol? Capture(int level, VarExpressionNode node)
	{
		var frame = _lambdaFrames[level];
		var name = node.Identifier.Text;
		if (frame.Bindings.TryGetValue(name, out var existing))
			return existing;
		
		if (frame.Target is { IsRef: false } target)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"Cannot capture '{name}' in '{target.Name}'"));
			
			frame.RejectsCaptures = true;
			return null;
		}
		
		var outer = frame.Outer.ResolveNear(name);
		var captured = outer is CapturedSymbol ? Capture(level - 1, node) : outer as VariableSymbol;
		if (captured is null)
			return null;
		
		var binding = new LocalVariableSymbol(node.Identifier, _typePool.GetPointerType(GetValueType(captured)!), false)
		{
			IsBorrowBinding = true,
			IsMutBinding = IsWritable(captured),
			Captured = captured
		};
		
		frame.Bindings[name] = binding;
		frame.Captures.Add((binding, node));
		Journal(() =>
		{
			frame.Bindings.Remove(name);
			frame.Captures.RemoveAt(frame.Captures.Count - 1);
		});
		
		return binding;
	}
	
	private static bool IsWritable(VariableSymbol variable) => variable switch
	{
		LocalVariableSymbol { IsBorrowBinding: true } binding => binding.IsMutBinding,
		LocalVariableSymbol local => local.IsMutable,
		ParameterSymbol parameter => !parameter.Mode.IsReadOnly(),
		_ => false
	};
}
