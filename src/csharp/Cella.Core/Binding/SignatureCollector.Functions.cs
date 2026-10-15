using System.Collections.Immutable;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void CollectConstructor(FunctionSymbol function, ConstructorNode node, ResolutionContext context)
	{
		var containingType = context.ContainingType!;
		var visibility = GetEffectiveVisibility(function, context);
		ReportDuplicateParameters(node.Parameters);
		ReportBorrowParameters(node.Parameters, false);
		ReportVariadicParameters(node.Parameters, null);
		
		var scope = new Scope();
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length + 1);
		
		var selfSymbol = function.Parameters[0];
		var selfType = _typePool.GetPointerType(containingType);
		
		paramTypes.Add(selfType);
		_builder.VariableTypes[selfSymbol] = selfType;
		scope.Define(selfSymbol);
		
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i + 1]; // + 1 due to implicit self parameter
			var paramType = _typePool.GetPassedType(
				RejectValueDyn(param.Type, context.ResolveType(param.Type), paramSymbol.Mode), paramSymbol.Mode);
			
			if (paramSymbol.Mode == ParameterMode.ReadOnly && !param.IsVariadic)
				_copiedParameters.Add(paramType, param.Identifier.SourceLocation);
			
			ReportHiddenType(param.Type, paramType, visibility, node.Keyword.Text);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		var defaults = CollectDefaults(node.Parameters, paramTypes[1..], context,
			context.Trait is null ? null : "traits");
		
		var signature = new FunctionSignature(paramTypes, NativeSymbols.Void, false, GetModes(function),
			TakesVariadicParameter(node.Parameters, paramTypes), defaults);
		
		var (mangledName, mangledBase) = context.Mangle(function, signature);
		function.QualifiedName = context.Qualify(function);
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File)
		{
			MangledBase = mangledBase
		};
		
		_builder.Functions[function] = info;
		if (context.Trait is { } trait)
			_typePool.AddTraitConstructor(trait, info);
		else
			_typePool.AddConstructor(containingType, info);
	}
	
	private void CollectDestructor(FunctionSymbol function, ResolutionContext context)
	{
		var self = function.Parameters[0];
		var selfType = _typePool.GetPointerType(context.ContainingType!);
		_builder.VariableTypes[self] = selfType;
		
		var scope = new Scope();
		scope.Define(self);
		
		var signature = new FunctionSignature([selfType], NativeSymbols.Void, false, GetModes(function));
		var (mangledName, mangledBase) = context.Mangle(function, signature);
		function.QualifiedName = context.Qualify(function);
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File)
		{
			MangledBase = mangledBase
		};
		
		_builder.Functions[function] = info;
		_typePool.SetDestructor(context.ContainingType!, info);
	}
	
	private FunctionInfo CollectFunction(FunctionSymbol function, FunctionNode node, ResolutionContext context)
	{
		ReportWhenFunction(node, context);
		Diagnostics.AddRange(ReportDuplicateTypeParameters(node.TypeParameters));
		
		ReportDuplicateParameters(node.Parameters);
		ReportParameterNameClashes(node.TypeParameters, node.Parameters);
		ReportBorrowParameters(node.Parameters, node.IsExternal, function.Property?.Node?.Type);
		ReportVariadicParameters(node.Parameters, node.IsExternal ? "'ext' functions"
			: node.Identifier.Type != TokenType.Identifier ? "operators"
			: null);
		
		var scope = new Scope();
		
		var paramTypes = new List<TypeSymbol>(function.Parameters.Length);
		if (node.Receiver is { } receiver)
		{
			var self = function.Parameters[0];
			var selfType = context.ContainingType is { } owner
				? _typePool.GetPassedType(owner, self.Mode)
				: NativeSymbols.Invalid;
			
			if (context.ContainingType is null)
				Diagnostics.Add(new(DiagnosticSeverity.Error, receiver.SourceLocation,
					"Cannot take 'self' outside types"));
			else if (self.Mode == ParameterMode.ReadOnly)
				_copiedParameters.Add(selfType, receiver.Self.SourceLocation);
			
			paramTypes.Add(selfType);
			_builder.VariableTypes[self] = selfType;
			scope.Define(self);
		}
		
		var visibility = GetEffectiveVisibility(function, context);
		var name = function.Property?.Name ?? node.Identifier.Text;
		var reportsHiddenTypes = function.Property?.Node?.Keyword is null;
		var offset = paramTypes.Count;
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i + offset];
			var paramType = _typePool.GetPassedType(
				RejectValueDyn(param.Type, ResolveSignatureType(function, param.Type, context), paramSymbol.Mode),
				paramSymbol.Mode);
			
			if (paramSymbol.Mode == ParameterMode.ReadOnly && !function.IsExternal && !param.IsVariadic)
				_copiedParameters.Add(paramType, param.Identifier.SourceLocation);
			
			if (reportsHiddenTypes)
				ReportHiddenType(param.Type, paramType, visibility, name);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = RejectValueDyn(returnTypeSyntax,
				ResolveSignatureType(function, returnTypeSyntax, context, isReturn: true));
		
		if (reportsHiddenTypes && node.ReturnType is { } returnTypeNode)
			ReportHiddenType(returnTypeNode, returnType, visibility, name);
		
		var hasVariadicParameter = !node.IsExternal && node.Identifier.Type == TokenType.Identifier &&
		                           TakesVariadicParameter(node.Parameters, paramTypes);
		
		var defaults = CollectDefaults(node.Parameters, paramTypes[offset..], context,
			context.Trait is not null ? "traits"
			: node.Identifier.Type != TokenType.Identifier ? "operators"
			: null);
		
		var signature = new FunctionSignature(paramTypes, returnType, false, GetModes(function), hasVariadicParameter,
			defaults);
		
		// TODO Disable mangling if indicated
		var (mangledName, mangledBase) = context.Mangle(function, signature);
		function.QualifiedName = context.Qualify(function);
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File)
		{
			MangledBase = mangledBase
		};
		
		if (_entryPointName is not null && function.Name == _entryPointName && function.TypeParameters.IsEmpty &&
		    IsEntryPoint(signature))
		{
			_entryPoints.Add(info);
		}
		
		return info;
	}
	
	private void ReportWhenFunction(FunctionNode node, ResolutionContext context)
	{
		if (node.When is not { } when)
			return;
		
		if (node.Identifier.Type != TokenType.Identifier)
			Diagnostics.Add(new(DiagnosticSeverity.Error, when.Keyword.SourceLocation,
				"Cannot use 'when' on operators"));
		else if (context.Trait is not null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, when.Keyword.SourceLocation, "Cannot use 'when' in traits"));
		else if (node.IsExternal)
			Diagnostics.Add(new(DiagnosticSeverity.Error, when.Keyword.SourceLocation,
				"Cannot use 'when' on 'ext' functions"));
		else if (node.Identifier.Text == _entryPointName && context.ContainingType is null &&
		         context.ImplBlock is null)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, when.Keyword.SourceLocation,
				$"Cannot use 'when' on '{_entryPointName}'"));
			
			return;
		}
		
		if (node.ReturnType is { } returnType)
			Diagnostics.Add(new(DiagnosticSeverity.Error, returnType.SourceLocation,
				returnType is IdentifierTypeNode { Token.Text: "never" }
					? "Cannot use 'when' on 'never' functions"
					: "Cannot return values from 'when' functions"));
		
		var modes = node.Parameters.Select(static parameter => parameter.Mode).Prepend(node.Receiver?.Mode);
		foreach (var mode in modes.OfType<Token>().Where(static mode => mode.Type == TokenType.KeywordOwn))
			Diagnostics.Add(new(DiagnosticSeverity.Error, mode.SourceLocation,
				"Cannot take 'own' parameters in 'when' functions"));
	}
	
	private FunctionInfo CollectExternalFunction(FunctionSymbol function, ExternalFunctionNode node,
		ResolutionContext context)
	{
		ReportDuplicateParameters(node.Parameters);
		ReportBorrowParameters(node.Parameters, true);
		ReportTypedVariadics(node);
		
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length);
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i];
			var paramType = _typePool.GetPassedType(context.ResolveType(param.Type), paramSymbol.Mode);
			
			ReportHiddenType(param.Type, paramType, function.Visibility, node.Identifier.Text);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = context.ResolveReturnType(returnTypeSyntax);
		
		if (node.ReturnType is { } returnTypeNode)
			ReportHiddenType(returnTypeNode, returnType, function.Visibility, node.Identifier.Text);
		
		var defaults = CollectDefaults(node.Parameters, paramTypes, context, null);
		var signature = new FunctionSignature(paramTypes, returnType, node.IsVariadic, GetModes(function), false,
			defaults);
		
		return new(null, function, signature, null, node.Origin, context.File);
	}
	
	private static FunctionInfo CreateInvalidInfo(FunctionSymbol function, Declaration declaration)
	{
		var parameterTypes = function.Parameters.Select(static _ => (TypeSymbol)NativeSymbols.Invalid);
		var signature = new FunctionSignature(parameterTypes, NativeSymbols.Invalid, false, GetModes(function));
		return new(null, function, signature, new Scope(), null, declaration.Context.File);
	}
	
	private static IEnumerable<ParameterMode> GetModes(FunctionSymbol function) =>
		function.Parameters.Select(static p => p.Mode);
	
	private bool Enter(Symbol symbol, bool isValue)
	{
		var index = _inProgress.IndexOf((symbol, isValue));
		if (index < 0)
		{
			_inProgress.Add((symbol, isValue));
			return true;
		}
		
		var cycle = _inProgress.Skip(index).ToList();
		foreach (var (member, _) in cycle)
			if (_cyclic.Add(member))
				ReportCycle(member, cycle.Contains((member, true)));
		
		return false;
	}
	
	private void Exit() => _inProgress.RemoveAt(_inProgress.Count - 1);
	
	private void ReportCycle(Symbol symbol, bool isValue)
	{
		var (identifier, message) = symbol switch
		{
			GlobalSymbol global => (global.Syntax.Identifier, $"'{global.Name}' depends on its own value"),
			RecordSymbol record => (record.Node.Identifier, $"'{record.Name}' depends on its own layout"),
			EnumSymbol enumType => (enumType.Node.Identifier, isValue
				? $"'{enumType.Name}' depends on its own values"
				: $"'{enumType.Name}' depends on its own layout"),
			FunctionSymbol function => (GetIdentifier(function.Syntax),
				$"'{function.Name}' depends on its own signature"),
			_ => throw new InvalidOperationException()
		};
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, identifier.SourceLocation, message));
	}
	
	private void ReportVariadicParameters(ImmutableArray<ParameterNode> parameters, string? place)
	{
		for (var i = 0; i < parameters.Length; i++)
		{
			if (parameters[i].Ellipsis is not { } ellipsis)
				continue;
			
			if (place is not null)
				Diagnostics.Add(new(DiagnosticSeverity.Error, ellipsis.SourceLocation,
					$"Cannot take variadic parameters in {place}"));
			
			if (parameters[i].Mode is { } mode)
				Diagnostics.Add(new(DiagnosticSeverity.Error, mode.SourceLocation,
					$"Cannot take '{mode.Text}' variadic parameters"));
			
			if (i + 1 < parameters.Length)
				Diagnostics.Add(new(DiagnosticSeverity.Error, parameters[i + 1].Identifier.SourceLocation,
					"Cannot take parameters after variadic parameters"));
		}
	}
	
	private List<Constant> CollectDefaults(ImmutableArray<ParameterNode> parameters, List<TypeSymbol> types,
		ResolutionContext context, string? place)
	{
		var defaults = new List<Constant>();
		for (var i = 0; i < parameters.Length; i++)
		{
			var parameter = parameters[i];
			if (parameter.DefaultValue is not { } value)
			{
				if (defaults.Count > 0 && !parameter.IsVariadic)
				{
					Diagnostics.Add(new(DiagnosticSeverity.Error, parameter.Identifier.SourceLocation,
						"Cannot take parameters without defaults after parameters with defaults"));
					
					defaults.Clear();
				}
				
				continue;
			}
			
			if (place is not null)
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, value.SourceLocation,
					$"Cannot declare defaults in {place}"));
				
				continue;
			}
			
			if (parameter.IsVariadic)
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, value.SourceLocation,
					"Cannot declare defaults for variadic parameters"));
				
				continue;
			}
			
			if (parameter.Mode is { Type: TokenType.KeywordMut })
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, value.SourceLocation,
					"Cannot declare defaults for 'mut' parameters"));
				
				defaults.Add(InvalidConstant.Instance);
				continue;
			}
			
			var type = types[i] is FunctionType { IsRef: true } passed &&
			           parameter.Type is not FunctionTypeNode { IsRef: true }
				? _typePool.GetPlainFunctionType(passed)
				: types[i];
			
			var resolved = type is InvalidType ? null : constants!.ResolveInitializer(value, type, context);
			var constant = resolved is null
				? InvalidConstant.Instance
				: EvaluateChecked(resolved) ?? (IsParametric(resolved) ? new ParametricConstant(resolved) : null);
			
			if (constant is null)
				Diagnostics.Add(_typePool.HoldsBorrows(type)
					? new(DiagnosticSeverity.Error, parameter.Type.SourceLocation,
						"Cannot declare defaults for parameters that hold borrows")
					: new(DiagnosticSeverity.Error, value.SourceLocation,
						$"The default value of '{parameter.Identifier.Text}' must be a constant"));
			
			defaults.Add(constant ?? InvalidConstant.Instance);
		}
		
		return defaults;
	}
	
	private bool IsParametric(IResolvedExpressionNode node) => node switch
	{
		ResolvedSizeOfExpressionNode or ResolvedAlignOfExpressionNode or ResolvedValueParameterExpressionNode => true,
		ResolvedUnaryOpExpressionNode { Operation: NativeImpl } n => IsParametric(n.Operand),
		ResolvedBinaryOpExpressionNode { Operation: NativeImpl } n => IsParametricOperand(n.Left) &&
		                                                              IsParametricOperand(n.Right),
		ResolvedConversionExpressionNode n => IsParametric(n.Source),
		_ => false
	};
	
	private bool IsParametricOperand(IResolvedExpressionNode node) =>
		Evaluator.Evaluate(node) is not null || IsParametric(node);
	
	private bool TakesVariadicParameter(ImmutableArray<ParameterNode> parameters, List<TypeSymbol> types)
	{
		if (parameters is not [.., { IsVariadic: true } last])
			return false;
		
		if (last.Mode is null)
			_variadicTypes.Add((types[^1], last.Type));
		
		return true;
	}
	
	private void ReportTypedVariadics(ExternalFunctionNode node)
	{
		foreach (var parameter in node.Parameters)
		{
			if (parameter.Ellipsis is not { } ellipsis)
				continue;
			
			var (source, range) = ellipsis.SourceLocation;
			Diagnostics.Add(new(DiagnosticSeverity.Error, new(source, range.Join(parameter.Type.SourceLocation.Range)),
				"Cannot take typed variadic parameters in 'ext' functions")
			{
				Hints = node.Parameters is [_, _, ..] && parameter == node.Parameters[^1] && !node.IsVariadic
					? ["Did you mean '...'?"]
					: []
			});
		}
	}
	
	private void ReportVariadicTypes()
	{
		foreach (var (type, node) in _variadicTypes.Where(static entry => entry.Type is not InvalidType))
		{
			var message = _typePool.FindRunElements(type).Count switch
			{
				0 => $"'{type.Name}' doesn't meet 'new(ptr[T], usize)'",
				1 => null,
				_ => $"'{type.Name}' meets 'new(ptr[T], usize)' more than once"
			};
			
			if (message is not null)
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, message)
				{
					Hints = _typePool.DescribeHiddenRun(type) is { } hint ? [hint] : []
				});
		}
		
		_variadicTypes.Clear();
	}
	
	private void ReportParameterNameClashes(ImmutableArray<TypeParameterNode> typeParameters,
		ImmutableArray<ParameterNode> parameters)
	{
		var names = parameters.Select(static p => p.Identifier.Text).ToHashSet();
		var clashes = typeParameters.Select(static p => p.Identifier.Text).Where(names.Contains).ToHashSet();
		foreach (var identifier in typeParameters.Select(static p => p.Identifier)
			         .Concat(parameters.Select(static p => p.Identifier))
			         .Where(identifier => clashes.Contains(identifier.Text)))
			Diagnostics.Add(new(DiagnosticSeverity.Error, identifier.SourceLocation,
				$"'{identifier.Text}' is declared more than once"));
	}
	
	private void ReportDuplicateParameters(IEnumerable<ParameterNode> parameters) =>
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(parameters.Select(static p => p.Identifier),
			static name => $"Parameter '{name}' is declared more than once"));
	
	private void ReportBorrowParameters(IEnumerable<ParameterNode> parameters, bool isExternal,
		ITypeNode? sharedType = null)
	{
		foreach (var parameter in parameters)
		{
			var diagnostic = parameter.Type switch
			{
				BorrowTypeNode borrow => DiagnosticReporter.ReportBorrowParameter(borrow, parameter.Identifier.Text,
					parameter.Mode is not null, isExternal),
				FunctionTypeNode { IsRef: true } function => DiagnosticReporter.ReportRefFunctionParameter(function,
					parameter.Identifier.Text, parameter.Mode is not null, isExternal),
				_ => null
			};
			
			if (diagnostic is null)
				continue;
			
			Diagnostics.Add(parameter.Type == sharedType
				? new(DiagnosticSeverity.Error, parameter.Identifier.SourceLocation, diagnostic.Message)
				: diagnostic);
		}
	}
}