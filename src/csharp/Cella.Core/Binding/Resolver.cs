using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Syntax.Nodes.Declarations;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class Resolver : ISyntaxNodeVisitor<IResolvedNode>
{
	private readonly Dictionary<FunctionSymbol, FunctionInfo> _importedFunctions = [];
	private readonly Stack<TypeSymbol?> _targetTypes = [];
	private readonly SymbolTable _symbolTable;
	private readonly SignatureTable _assemblySignatureTable;
	private readonly SignatureTable _dependencySignatureTable;
	private readonly Stack<ResolutionContext> _resolutionContexts = [];
	private ResolutionContext CurrentResolutionContext => _resolutionContexts.Peek();
	private FunctionInfo CurrentFunction => CurrentResolutionContext.ContainingFunction!.Value;
	private Scope? CurrentScope => CurrentResolutionContext.LocalScope;
	
	public Resolver(AssemblySymbol assemblySymbol, IEnumerable<AssemblySymbol> dependencies)
	{
		_symbolTable = assemblySymbol.SymbolTable;
		_assemblySignatureTable = assemblySymbol.SignatureTable;
		_dependencySignatureTable = SignatureTable.Combine(dependencies.Select(static a => a.SignatureTable));
	}
	
	private TypeSymbol? CurrentTargetType => _targetTypes.Peek();
	
	public ResolvedFileNode Resolve(FileNode root) => (ResolvedFileNode)Visit(root);
	
	private IResolvedDeclarationNode VisitNode(IDeclarationNode node) =>
		(IResolvedDeclarationNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedStatementNode VisitNode(IStatementNode node) =>
		(IResolvedStatementNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedExpressionNode VisitNode(IExpressionNode node) =>
		(IResolvedExpressionNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	public IResolvedNode Visit(FileNode node)
	{
		var file = (FileSymbol)_symbolTable.DeclarationSymbols[node];
		var imports = _assemblySignatureTable.ImportEnvironments[file];
		
		var resolutionContext = new ResolutionContext
		{
			File = file,
			Imports = imports
		};
		
		_resolutionContexts.Push(resolutionContext);
		
		var resolvedDeclarations = new List<IResolvedDeclarationNode>(node.Declarations.Length);
		foreach (var declaration in node.Declarations)
			resolvedDeclarations.Add(VisitNode(declaration));
		
		_resolutionContexts.Pop();
		
		var result = new ResolvedFileNode(file, resolvedDeclarations, _importedFunctions.Values);
		_importedFunctions.Clear();
		return result;
	}
	
	public IResolvedNode Visit(FunctionNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var info = _assemblySignatureTable.Functions[function];
		
		var resolutionContext = CurrentResolutionContext with
		{
			ContainingFunction = info,
			LocalScope = info.Scope
		};
		
		_resolutionContexts.Push(resolutionContext);
		var body = Visit(node.Body);
		_resolutionContexts.Pop();
		
		return new ResolvedFunctionNode(info, body);
	}
	
	public IResolvedNode Visit(BlockStatementNode node)
	{
		var statements = new List<IResolvedStatementNode>(node.StatementNodes.Length);
		var scope = CurrentScope?.CreateChild();
		
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		_resolutionContexts.Push(resolutionContext);
		foreach (var child in node.StatementNodes)
			statements.Add(VisitNode(child));
		
		_resolutionContexts.Pop();
		
		return new ResolvedBlockStatementNode(statements);
	}
	
	public IResolvedNode Visit(ReturnStatementNode node)
	{
		var info = _assemblySignatureTable.Functions[CurrentFunction.Symbol];
		_targetTypes.Push(info.Signature.ReturnType);
		
		var result = new ResolvedReturnStatementNode(node.ExpressionNode is { } expressionNode
			? VisitNode(expressionNode)
			: null);
		
		_targetTypes.Pop();
		return result;
	}
	
	public IResolvedNode Visit(ExpressionStatementNode node) =>
		new ResolvedExpressionStatementNode(VisitNode(node.ExpressionNode));
	
	public IResolvedNode Visit(CallExpressionNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var functionName = node.Identifier.GetText();
		var symbol = resolutionContext.Resolve(functionName);
		
		// TODO Diagnostics, emit invalid expression instead of throwing exceptions
		if (symbol is null)
			throw new Exception($"Symbol '{functionName}' not found in this scope");
		
		if (symbol is not FunctionSymbol function)
			throw new Exception($"Symbol '{functionName}' is not a function");
		
		if (!_assemblySignatureTable.Functions.TryGetValue(function, out var info))
		{
			info = _dependencySignatureTable.Functions[function];
			_importedFunctions.TryAdd(function, info);
		}
		
		return new ResolvedFunctionCallExpressionNode(info, node.Arguments.Select(VisitNode));
	}
	
	public IResolvedNode Visit(LiteralExpressionNode node)
	{
		var valueSpan = node.Token.AsSpan();
		
		TypeSymbol? type = null;
		object? value = null;
		
		if (node.Token.Type == TokenType.IntegerLiteral)
			(type, value) = ParseInteger(valueSpan, CurrentTargetType);
		
		// TODO We should emit diagnostics here
		type ??= NativeSymbols.Invalid;
		
		return new ResolvedLiteralExpressionNode(type, value);
	}
	
	public IResolvedNode Visit(UnaryOpExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		_targetTypes.Push(null);
		var operand = VisitNode(node.Operand);
		_targetTypes.Pop();
		
		UnaryOperation op;
		if (node.Op.Type == TokenType.OpPlus)
			op = UnaryOperation.Identity;
		else if (node.Op.Type == TokenType.OpMinus)
			op = UnaryOperation.Negation;
		else
			throw new InvalidOperationException();
		
		if (NativeOperations.Resolve(op, operand.Type) is { } nativeType)
		{
			// We optimize away identity operations if it's a native type since it's a no-op
			if (op == UnaryOperation.Identity)
				return operand;
			
			return new ResolvedUnaryOpExpressionNode(nativeType, op, operand);
		}
		
		// TODO Based on type of sub-expression and the op token, we search for operator overloads
		
		return new ResolvedUnaryOpExpressionNode(NativeSymbols.Invalid, op, operand);
	}
	
	public IResolvedNode Visit(VarExpressionNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var varName = node.Identifier.GetText();
		var symbol = resolutionContext.Resolve(varName);
		
		// TODO Diagnostics, emit invalid expression instead of throwing exceptions
		if (symbol is null)
			throw new Exception($"Symbol '{varName}' not found in this scope");
		
		switch (symbol)
		{
			case LocalVariableSymbol v:
				return new ResolvedVarExpressionNode(v, v.Type);
			
			case VariableSymbol v:
				if (!_assemblySignatureTable.VariableTypes.TryGetValue(v, out var type))
					type = _dependencySignatureTable.VariableTypes[v];
				
				return new ResolvedVarExpressionNode(v, type);
			
			default:
				throw new Exception($"Symbol '{varName}' is not a variable");
		}
	}
	
	public IResolvedNode Visit(VarStatementNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		
		TypeSymbol? type;
		if (node.Type is { } specifiedType)
			type = resolutionContext.Resolve(specifiedType.GetText()) as TypeSymbol;
		else
			type = null;
		
		IResolvedExpressionNode? initializer;
		if (node.ExpressionNode is { } initializerNode)
		{
			_targetTypes.Push(type);
			initializer = VisitNode(initializerNode);
			_targetTypes.Pop();
		}
		else
			initializer = null;
		
		type ??= initializer?.Type ?? NativeSymbols.Invalid;
		
		var symbol = new LocalVariableSymbol(node, type);
		resolutionContext.LocalScope!.Define(symbol);
		
		return new ResolvedVarStatementNode(symbol, initializer);
	}
	
	public IResolvedNode Visit(BinaryOpExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		_targetTypes.Push(null);
		var left = VisitNode(node.Left);
		var right = VisitNode(node.Right);
		_targetTypes.Pop();
		
		BinaryOperation op;
		if (node.Op.Type == TokenType.OpPlus)
			op = BinaryOperation.Addition;
		else if (node.Op.Type == TokenType.OpMinus)
			op = BinaryOperation.Subtraction;
		else if (node.Op.Type == TokenType.OpStar)
			op = BinaryOperation.Multiplication;
		else if (node.Op.Type == TokenType.OpSlash)
			op = BinaryOperation.Division;
		else if (node.Op.Type == TokenType.OpEqual)
			op = BinaryOperation.Assignment;
		else if (node.Op.Type == TokenType.OpPlusEqual)
			op = BinaryOperation.AddAssignment;
		else if (node.Op.Type == TokenType.OpMinusEqual)
			op = BinaryOperation.SubtractAssignment;
		else if (node.Op.Type == TokenType.OpStarEqual)
			op = BinaryOperation.MultiplyAssignment;
		else if (node.Op.Type == TokenType.OpSlashEqual)
			op = BinaryOperation.DivideAssignment;
		else
			throw new InvalidOperationException();
		
		// TODO How to handle implicit upcasts..?
		if (NativeOperations.Resolve(left.Type, op, right.Type) is { } nativeType)
			return new ResolvedBinaryOpExpressionNode(nativeType, left, op, right);
		
		// TODO Based on types of sub-expressions and the op token, we search for operator overloads
		
		return new ResolvedBinaryOpExpressionNode(NativeSymbols.Invalid, left, op, right);
	}
	
	private static (TypeSymbol? Type, object? Value) ParseInteger(ReadOnlySpan<char> span, TypeSymbol? targetType)
	{
		// TODO Check suffixes
		
		if (targetType is PrimitiveType primitiveType)
			switch (primitiveType.Kind)
			{
				case PrimitiveTypeKind.Int32:
					if (int.TryParse(span, out var intValue))
						return (NativeSymbols.Int32, intValue);
					
					break;
				
				case PrimitiveTypeKind.Int64:
					if (long.TryParse(span, out var longValue))
						return (NativeSymbols.Int64, longValue);
					
					break;
				
				case PrimitiveTypeKind.Int128:
					if (Int128.TryParse(span, out var int128Value))
						return (NativeSymbols.Int128, int128Value);
					
					break;
			}
		
		if (int.TryParse(span, out var intValue2))
			return (NativeSymbols.Int32, intValue2);
		
		if (long.TryParse(span, out var longValue2))
			return (NativeSymbols.Int64, longValue2);
		
		if (Int128.TryParse(span, out var int128Value2))
			return (NativeSymbols.Int128, int128Value2);
		
		return (null, null);
	}
}