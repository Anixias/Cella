using System.Collections.Immutable;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private static bool IsDereference(FunctionNode node) =>
		node is { Identifier.Type: TokenType.OpStar, Receiver: not null };
	
	private static bool IsIndexer(FunctionNode node) => node.Identifier.Type == TokenType.OpOpenBracket;
	
	private static bool IsLoopOperator(FunctionNode node) =>
		node.Identifier.Type is TokenType.KeywordIn or TokenType.KeywordFor;
	
	private static bool IsConversion(FunctionNode node) =>
		node.Identifier.Type is TokenType.KeywordAs or TokenType.KeywordNew;
	
	private static bool IsModeOperator(FunctionNode node) =>
		IsDereference(node) || IsIndexer(node) || IsLoopOperator(node) || IsConversion(node);
	
	private void ReportModeOperators(TypeSymbol type, IReadOnlyList<FunctionNode> operators)
	{
		ReportOperatorParameters(operators);
		var kinds = operators.ToLookup(static o => o.Identifier.Type);
		ReportDereferences(type, [..kinds[TokenType.OpStar]]);
		ReportIndexers(type, [..kinds[TokenType.OpOpenBracket]]);
		ReportLoopOperators(type, [..kinds[TokenType.KeywordIn], ..kinds[TokenType.KeywordFor]]);
		ReportConversions(type, [..kinds[TokenType.KeywordAs], ..kinds[TokenType.KeywordNew]]);
	}
	
	private void ReportConversions(TypeSymbol type, List<FunctionNode> conversions)
	{
		var outward = new List<(FunctionNode Node, FunctionSignature Signature)>();
		var inward = new List<(FunctionNode Node, FunctionSignature Signature)>();
		foreach (var conversion in conversions)
		{
			var info = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[conversion]];
			var signature = info.Signature;
			if (signature.ReturnType is InvalidType || signature.ParameterTypes.Any(static p => p is InvalidType))
				continue;
			
			if ((FindConversionError(type, conversion, signature) ??
			     FindBuiltInConversion(type, conversion, signature)) is var (location, message))
			{
				ReportOn(conversion, new(DiagnosticSeverity.Error, location, message));
				continue;
			}
			
			(conversion.Receiver is null ? inward : outward).Add((conversion, signature));
			if (conversion.Identifier.Type == TokenType.KeywordNew &&
			    type is (RecordSymbol or EnumSymbol) and not NamedTypeSymbol { IsGenericInstance: true })
				_typePool.AddExplicitConversion(type, info);
		}
		
		foreach (var sameTarget in outward.GroupBy(static c => GetIndexedType(c.Signature)))
		{
			if (sameTarget.Select(static c => c.Node.Identifier.Type).Distinct().Count() > 1)
			{
				var message = $"Cannot declare both 'as' and 'new' conversions to '{sameTarget.Key.Name}'";
				foreach (var c in sameTarget)
					ReportOn(c.Node, new(DiagnosticSeverity.Error, c.Node.Identifier.SourceLocation, message));
				
				continue;
			}
			
			var sameModes = sameTarget.GroupBy(static c => c.Signature.GetMode(0)).Where(static g => g.Count() > 1);
			foreach (var sameMode in sameModes)
			{
				var receiver = DescribeConversionReceiver(sameMode.Key);
				var message = $"Conversion to '{sameTarget.Key.Name}' with '{receiver}' is declared more than " +
				              $"once in '{type.Name}'";
				
				foreach (var c in sameMode)
					ReportOn(c.Node, new(DiagnosticSeverity.Error, c.Node.Identifier.SourceLocation, message));
			}
		}
		
		var sameSources = inward.GroupBy(static c => c.Signature.ParameterTypes[0]).Where(static g => g.Count() > 1);
		foreach (var sameSource in sameSources)
		{
			var message = $"Conversion from '{sameSource.Key.Name}' is declared more than once in '{type.Name}'";
			foreach (var c in sameSource)
				ReportOn(c.Node, new(DiagnosticSeverity.Error, c.Node.Identifier.SourceLocation, message));
		}
	}
	
	private static (SourceLocation Location, string Message)? FindConversionError(TypeSymbol type, FunctionNode node,
		FunctionSignature signature)
	{
		var name = node.Identifier.Text;
		var result = signature.ReturnType;
		var resultLocation = node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation;
		if (node.Receiver is null)
		{
			return node switch
			{
				{ Parameters.Length: not 1 } =>
					(node.Identifier.SourceLocation, $"'{name}' operators need 'self' or one parameter"),
				{ ReturnType: null } => (node.Identifier.SourceLocation, $"'{name}' operators need a return type"),
				_ when result != type =>
					(resultLocation, $"Cannot return '{result.Name}' from '{name}' operators without 'self'"),
				_ when signature.ParameterTypes[0] == type =>
					(node.Parameters[0].SourceLocation, $"Cannot convert '{type.Name}' to itself"),
				_ => null
			};
		}
		
		var mode = signature.GetMode(0);
		return node switch
		{
			{ Parameters: [var parameter, ..] } =>
				(parameter.SourceLocation, $"Cannot take parameters in '{name}' operators with 'self'"),
			{ ReturnType: null } => (node.Identifier.SourceLocation, $"'{name}' operators need a return type"),
			_ when result is BorrowType { IsMutable: var isMutable } && isMutable != (mode == ParameterMode.Mut) ||
			       result is not (BorrowType or NamedTypeSymbol { IsRef: true }) && mode == ParameterMode.Mut =>
				(resultLocation,
					$"Cannot return '{result.Name}' from '{name}' with '{DescribeConversionReceiver(mode)}'"),
			_ when GetIndexedType(signature) == type => (resultLocation, $"Cannot convert '{type.Name}' to itself"),
			_ => null
		};
	}
	
	private (SourceLocation Location, string Message)? FindBuiltInConversion(TypeSymbol type, FunctionNode node,
		FunctionSignature signature)
	{
		if (node.Receiver is null)
			return IsBuiltIn(signature.ParameterTypes[0], type)
				? (node.Parameters[0].SourceLocation,
					$"Conversion from '{signature.ParameterTypes[0].Name}' is built in")
				: null;
		
		var result = signature.ReturnType;
		return result is not BorrowType && IsBuiltIn(type, result)
			? (node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
				$"Conversion to '{result.Name}' is built in")
			: null;
	}
	
	private bool IsBuiltIn(TypeSymbol from, TypeSymbol to) =>
		_typePool.ConversionTable.FindExplicit(from, to) is not (null or FunctionConversion);
	
	private static string DescribeConversionReceiver(ParameterMode mode) =>
		mode == ParameterMode.Mut ? "mut self" : "self";
	
	private void ReportLoopOperators(TypeSymbol type, List<FunctionNode> operators)
	{
		var valid = new List<(FunctionNode Node, FunctionSignature Signature)>();
		foreach (var node in operators)
		{
			var signature = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[node]].Signature;
			if (signature.ReturnType is InvalidType)
				continue;
			
			if (FindLoopOperatorError(node, signature) is var (location, message))
				ReportOn(node, new(DiagnosticSeverity.Error, location, message));
			else
				valid.Add((node, signature));
		}
		
		var sameModes = valid
			.GroupBy(static o => (o.Node.Identifier.Text, o.Signature.GetMode(0)))
			.Where(static g => g.Count() > 1);
		
		foreach (var sameMode in sameModes)
		{
			var (name, mode) = sameMode.Key;
			var message = $"'{name}' with '{DescribeReceiver(mode)}' is declared more than once in '{type.Name}'";
			foreach (var o in sameMode)
				ReportOn(o.Node, new(DiagnosticSeverity.Error, o.Node.Identifier.SourceLocation, message));
		}
	}
	
	private static (SourceLocation Location, string Message)? FindLoopOperatorError(FunctionNode node,
		FunctionSignature signature)
	{
		var name = node.Identifier.Text;
		var isStep = node.Identifier.Type == TokenType.KeywordFor;
		return node switch
		{
			{ Receiver: null } => (node.Identifier.SourceLocation,
				$"Cannot declare '{name}' operators without '{(isStep ? "mut self" : "self")}'"),
			{ Receiver: { Mode: null } receiver } when isStep =>
				(receiver.SourceLocation, "Cannot take 'self' in 'for' operators"),
			{ Parameters: [var parameter, ..] } =>
				(parameter.SourceLocation, $"Cannot take parameters in '{name}' operators"),
			{ ReturnType: null } => (node.Identifier.SourceLocation, $"'{name}' operators need a return type"),
			_ when !IsLoopOperatorResult(isStep, signature.ReturnType) =>
				(node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
					$"Cannot return '{signature.ReturnType.Name}' from '{name}' operators"),
			_ => null
		};
	}
	
	private static bool IsLoopOperatorResult(bool isStep, TypeSymbol type) => isStep
		? type == NativeSymbols.Bool
		: type != NativeSymbols.Void && type is not BorrowType;
	
	private void ReportIndexers(TypeSymbol type, List<FunctionNode> indexers)
	{
		var valid = new List<(FunctionNode Node, FunctionSignature Signature)>();
		foreach (var indexer in indexers)
		{
			var signature = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[indexer]].Signature;
			if (signature.ReturnType is InvalidType)
				continue;
			
			if (FindIndexerError(type, indexer, signature) is var (location, message))
				ReportOn(indexer, new(DiagnosticSeverity.Error, location, message));
			else
				valid.Add((indexer, signature));
		}
		
		var sameModes = valid.GroupBy(static i => i.Signature.GetMode(0)).Where(static g => g.Count() > 1).ToList();
		foreach (var sameMode in sameModes)
		{
			var message = $"'[]' with '{DescribeReceiver(sameMode.Key)}' is declared more than once in '{type.Name}'";
			foreach (var i in sameMode)
				ReportOn(i.Node, new(DiagnosticSeverity.Error, i.Node.Identifier.SourceLocation, message));
		}
		
		if (sameModes.Count > 0 || valid is not [var first, var aligned])
			return;
		
		var second = aligned with { Signature = AlignTypeParameters(first.Node, aligned.Node, aligned.Signature) };
		if (!HaveSameIndices(first.Signature, second.Signature))
			foreach (var i in valid)
				ReportOn(i.Node, new(DiagnosticSeverity.Error, i.Node.Identifier.SourceLocation,
					"Cannot declare '[]' operators with different parameters"));
		
		if (GetIndexedType(first.Signature) != GetIndexedType(second.Signature))
			foreach (var i in valid)
				ReportOn(i.Node, new(DiagnosticSeverity.Error, i.Node.ReturnType!.SourceLocation,
					"Cannot declare '[]' operators with different target types"));
	}
	
	private FunctionSignature AlignTypeParameters(FunctionNode model, FunctionNode node, FunctionSignature signature)
	{
		var from = ((FunctionSymbol)_symbolTable.DeclarationSymbols[node]).DeclaredTypeParameters;
		var to = ((FunctionSymbol)_symbolTable.DeclarationSymbols[model]).DeclaredTypeParameters;
		return from.IsEmpty || from.Length != to.Length
			? signature
			: _typePool.SubstituteSignature(signature, TypePool.CreateMap(from, [..to]));
	}
	
	private static TypeSymbol GetIndexedType(FunctionSignature signature) =>
		signature.ReturnType is BorrowType borrow ? borrow.Target : signature.ReturnType;
	
	private static bool IsIndexerResult(TypeSymbol type, FunctionSignature signature) => signature.ReturnType switch
	{
		BorrowType borrow => IsPlaceResult(type, borrow, signature.GetMode(0)),
		var result => signature.GetMode(0) != ParameterMode.Mut && result != NativeSymbols.Void
	};
	
	private static bool IsPlaceResult(TypeSymbol type, BorrowType result, ParameterMode mode) =>
		result.IsMutable == (mode == ParameterMode.Mut) ||
		result.IsMutable && type is NamedTypeSymbol { IsRef: true } or TypeParameterSymbol { IsSelf: true };
	
	private static bool HaveSameIndices(FunctionSignature first, FunctionSignature second) =>
		first.ParameterTypes.Length == second.ParameterTypes.Length &&
		Enumerable.Range(1, first.ParameterTypes.Length - 1).All(i =>
			first.GetDeclaredType(i) == second.GetDeclaredType(i) && first.GetMode(i) == second.GetMode(i));
	
	private static (SourceLocation Location, string Message)? FindIndexerError(TypeSymbol type, FunctionNode node,
		FunctionSignature signature) => node switch
	{
		{ Receiver: null } => (node.Identifier.SourceLocation, "Cannot declare '[]' operators without 'self'"),
		{ Parameters: [] } => (node.Identifier.SourceLocation, "'[]' operators need at least one parameter"),
		_ when node.Parameters.FirstOrDefault(static p => p.Mode?.Type == TokenType.KeywordMut) is { } parameter =>
			(parameter.SourceLocation, "Cannot take 'mut' parameters in '[]' operators"),
		{ ReturnType: null } => (node.Identifier.SourceLocation, "'[]' operators need a return type"),
		_ when IsIndexerResult(type, signature) => null,
		_ => (node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
			$"Cannot return '{signature.ReturnType.Name}' from '[]' with '{DescribeReceiver(signature.GetMode(0))}'")
	};
	
	private void ReportDereferences(TypeSymbol type, List<FunctionNode> dereferences)
	{
		var valid = new List<(FunctionNode Node, FunctionSignature Signature)>();
		foreach (var dereference in dereferences)
		{
			var signature = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[dereference]].Signature;
			if (signature.ReturnType is InvalidType)
				continue;
			
			if (FindDereferenceError(type, dereference, signature) is var (location, message))
				ReportOn(dereference, new(DiagnosticSeverity.Error, location, message));
			else
				valid.Add((dereference, signature));
		}
		
		foreach (var sameMode in valid.GroupBy(static d => d.Signature.GetMode(0)).Where(static g => g.Count() > 1))
		{
			var message = $"'*' with '{DescribeReceiver(sameMode.Key)}' is declared more than once in '{type.Name}'";
			foreach (var d in sameMode)
				ReportOn(d.Node, new(DiagnosticSeverity.Error, d.Node.Identifier.SourceLocation, message));
		}
		
		if (valid.Select(static d => GetIndexedType(d.Signature)).Distinct().Count() > 1)
			foreach (var d in valid)
				ReportOn(d.Node, new(DiagnosticSeverity.Error, d.Node.ReturnType!.SourceLocation,
					"Cannot declare '*' operators with different target types"));
	}
	
	private static (SourceLocation Location, string Message)? FindDereferenceError(TypeSymbol type, FunctionNode node,
		FunctionSignature signature) => node switch
	{
		{ Parameters: [var parameter, ..] } =>
			(parameter.SourceLocation, "Cannot take parameters in '*' operators with 'self'"),
		{ ReturnType: null } => (node.Identifier.SourceLocation, "'*' operators need a return type"),
		_ when IsIndexerResult(type, signature) => null,
		_ => (node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
			$"Cannot return '{signature.ReturnType.Name}' from '*' with '{DescribeReceiver(signature.GetMode(0))}'")
	};
	
	private static string DescribeReceiver(ParameterMode mode) => mode == ParameterMode.Mut ? "mut self" : "self";
	
	private static string DescribeReceiver(ReceiverNode receiver) =>
		receiver.Mode is { } mode ? $"{mode.Text} self" : "self";
	
	private void ReportOperatorParameters(IEnumerable<FunctionNode> operators)
	{
		foreach (var node in operators.Where(static node => !node.TypeParameters.IsEmpty))
		{
			var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
			var operands = _builder.Functions[function].Signature.ParameterTypes.Skip(node.Receiver is null ? 0 : 1);
			var inferred = operands.SelectMany(TypePool.FindTypeParameters).ToHashSet();
			for (var i = 0; i < node.TypeParameters.Length && i < function.DeclaredTypeParameters.Length; i++)
			{
				if (inferred.Contains(function.DeclaredTypeParameters[i]))
					continue;
				
				var parameter = node.TypeParameters[i].Identifier;
				ReportOn(node, new(DiagnosticSeverity.Error, parameter.SourceLocation,
					$"Cannot infer '{parameter.Text}' from the operands of '{node.Identifier.Text}'"));
			}
		}
	}
	
	private void ReportOperators(TypeSymbol type, IEnumerable<FunctionNode> functions)
	{
		var operators = functions.Where(static f => f.Identifier.Type != TokenType.Identifier).ToList();
		ReportOperatorParameters(operators);
		foreach (var node in operators)
		{
			var signature = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[node]].Signature;
			var error = FindOperatorError(type, node, signature) ?? FindBuiltInOperator(node, signature);
			if (error is var (location, message))
				ReportOn(node, new(DiagnosticSeverity.Error, location, message));
		}
	}
	
	private (SourceLocation Location, string Message)? FindBuiltInOperator(FunctionNode node,
		FunctionSignature signature)
	{
		TypeSymbol[] operands = [..signature.ParameterTypes.Select((_, i) => signature.GetDeclaredType(i))];
		var registry = _typePool.OperatorRegistry;
		var candidates = operands.Length == 1
			? registry.GetUnaryCandidates(node.Identifier.Type)
			: registry.GetBinaryCandidates(node.Identifier.Type);
		
		if (!candidates.OfType<OperationImpl>().Any(candidate => candidate.ParameterTypes.SequenceEqual(operands)))
			return null;
		
		var name = node.Identifier.Text;
		return (node.Identifier.SourceLocation, operands is [var left, var right]
			? $"Operator '{name}' between '{left.Name}' and '{right.Name}' is built in"
			: $"Operator '{name}' on '{operands[0].Name}' is built in");
	}
	
	private static (SourceLocation Location, string Message)? FindOperatorError(TypeSymbol type, FunctionNode node,
		FunctionSignature signature)
	{
		var name = node.Identifier.Text;
		if (_compoundOperators.Contains(node.Identifier.Type))
			return FindCompoundOperatorError(node);
		
		if (node.Identifier.Type == TokenType.OpQuestion)
			return FindQuestionOperatorError(type, node);
		
		if (node.Receiver is { } receiver)
		{
			if (!_unaryOperators.Contains(node.Identifier.Type))
				return (receiver.SourceLocation, $"Cannot take '{DescribeReceiver(receiver)}' in '{name}' operators");
			
			if (node.Parameters is [var parameter, ..])
				return (parameter.SourceLocation, $"Cannot take parameters in '{name}' operators with 'self'");
			
			return receiver.Mode?.Type == TokenType.KeywordMut
				? (receiver.SourceLocation, $"Cannot take 'mut self' in '{name}' operators")
				: null;
		}
		
		if (node.Identifier.Type == TokenType.OpTilde)
			return (node.Identifier.SourceLocation, $"Cannot declare '{name}' operators without 'self'");
		
		if (node.Parameters.Length != 2)
			return (node.Identifier.SourceLocation,
				_unaryOperators.Contains(node.Identifier.Type) || node.Identifier.Type == TokenType.OpStar
					? $"'{name}' operators need 'self' or two parameters"
					: $"'{name}' operators need two parameters");
		
		if (node.Parameters.FirstOrDefault(static p => p.Mode?.Type == TokenType.KeywordMut) is { } mutParameter)
			return (mutParameter.SourceLocation, $"Cannot take 'mut' parameters in '{name}' operators");
		
		return type is InvalidType || signature.GetDeclaredType(0) is InvalidType ||
		       signature.GetDeclaredType(1) is InvalidType || signature.GetDeclaredType(0) == type ||
		       signature.GetDeclaredType(1) == type
			? null
			: (node.Identifier.SourceLocation, $"'{name}' operators need a '{type.Name}' parameter");
	}
	
	private static (SourceLocation Location, string Message)? FindQuestionOperatorError(TypeSymbol type,
		FunctionNode node) => node switch
	{
		_ when type is TypeParameterSymbol =>
			(node.Identifier.SourceLocation, "Cannot declare '?' operators in traits"),
		{ Receiver: null } =>
			(node.Identifier.SourceLocation, "Cannot declare '?' operators without 'self'"),
		{ Receiver: { Mode: not null } receiver } =>
			(receiver.SourceLocation, $"Cannot take '{DescribeReceiver(receiver)}' in '?' operators"),
		{ Parameters: [var parameter, ..] } =>
			(parameter.SourceLocation, "Cannot take parameters in '?' operators with 'self'"),
		{ ReturnType: not null, Body: not ExpressionStatementNode } =>
			(node.Identifier.SourceLocation, "'?' operators must have an expression body"),
		_ => null
	};
	
	private static (SourceLocation Location, string Message)? FindCompoundOperatorError(FunctionNode node)
	{
		var name = node.Identifier.Text;
		return node switch
		{
			{ Receiver: null } =>
				(node.Identifier.SourceLocation, $"Cannot declare '{name}' operators without 'mut self'"),
			{ Receiver: { } receiver } when receiver.Mode?.Type != TokenType.KeywordMut =>
				(receiver.SourceLocation, $"Cannot take '{DescribeReceiver(receiver)}' in '{name}' operators"),
			{ Parameters.Length: not 1 } => (node.Identifier.SourceLocation, $"'{name}' operators need one parameter"),
			{ ReturnType: { } returnType } =>
				(returnType.SourceLocation, $"Cannot return values from '{name}' operators"),
			{ Parameters: [{ Mode.Type: TokenType.KeywordMut } parameter] } =>
				(parameter.SourceLocation, $"Cannot take 'mut' parameters in '{name}' operators"),
			_ => null
		};
	}
	
	private void ReportMemberConflicts(string owner,
		IEnumerable<(Token Name, IDeclarationNode? Member, MemberKind Kind)> members)
	{
		var sameNames = members
			.GroupBy(static member => member.Name.Text)
			.Where(static sameName => sameName.Count() > 1);
		
		foreach (var sameName in sameNames)
		{
			if (sameName.All(static member => member.Kind == MemberKind.Function))
			{
				List<IDeclarationNode> functions = [..sameName.Select(static member => member.Member!)];
				foreach (var function in functions)
				{
					if (FindConflict(function, functions) is { } conflict)
						ReportOn(function, conflict);
				}
				
				continue;
			}
			
			if (sameName.All(static member => member.Kind == MemberKind.Case))
				continue;
			
			var message = sameName.All(static member => member.Kind == MemberKind.Field)
				? $"Field '{sameName.Key}' is declared more than once in '{owner}'"
				: $"'{sameName.Key}' is declared more than once in '{owner}'";
			
			foreach (var member in sameName)
				ReportOn(member.Member, new(DiagnosticSeverity.Error, member.Name.SourceLocation, message));
		}
	}
	
	private void ReportConstructorConflicts(ImmutableArray<IDeclarationNode> members)
	{
		List<IDeclarationNode> constructors = [..members.OfType<ConstructorNode>()];
		foreach (var constructor in constructors)
		{
			if (FindConflict(constructor, constructors) is { } conflict)
				ReportOn(constructor, conflict);
		}
	}
}