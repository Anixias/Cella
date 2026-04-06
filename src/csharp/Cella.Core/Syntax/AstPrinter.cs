using System.Text;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Syntax.Nodes.Declarations;

namespace Cella.Core.Syntax;

public sealed class AstPrinter : ISyntaxNodeVisitor
{
	private readonly StringBuilder _sb = new();
	private readonly List<bool> _hasMoreSiblings = new();
	
	private AstPrinter()
	{
	}
	
	public static string Print(ISyntaxNode root)
	{
		var printer = new AstPrinter();
		((ISyntaxNodeVisitor)printer).Visit(root);
		return printer._sb.ToString();
	}
	
	private void StartLine(bool isLast)
	{
		if (_sb.Length == 0)
			return;
		
		_sb.AppendLine();
		
		for (var i = 0; i < _hasMoreSiblings.Count - 1; i++)
			_sb.Append(_hasMoreSiblings[i] ? "│   " : "    ");
		
		_sb.Append(isLast ? "└── " : "├── ");
	}
	
	private void VisitNode(ISyntaxNode node, bool isLast)
	{
		_hasMoreSiblings.Add(!isLast);
		((ISyntaxNodeVisitor)this).Visit(node);
		_hasMoreSiblings.RemoveAt(_hasMoreSiblings.Count - 1);
	}
	
	private void VisitAction(Action action, bool isLast)
	{
		_hasMoreSiblings.Add(!isLast);
		action();
		_hasMoreSiblings.RemoveAt(_hasMoreSiblings.Count - 1);
	}
	
	public void Visit(CallExpressionNode node)
	{
		StartLine();
		_sb.Append("CallExpressionNode '").Append(node.Identifier.AsSpan()).Append('\'');
		
		for (var i = 0; i < node.Arguments.Length; i++)
		{
			var last = i == node.Arguments.Length - 1;
			VisitNode(node.Arguments[i], last);
		}
	}
	
	public void Visit(ChainedExpressionNode node)
	{
		StartLine();
		_sb.Append("ChainedExpressionNode");
		
		var operandCount = node.Operands.Length;
		for (var i = 0; i < operandCount; i++)
		{
			var operand = node.Operands[i];
			var isLast = i == operandCount - 1;
			VisitNode(operand, isLast);
			
			if (isLast)
				continue;
			
			var op = node.Ops[i];
			VisitAction(() =>
			{
				StartLine();
				_sb.Append(op.AsSpan());
			}, isLast);
		}
	}
	
	public void Visit(FileNode node)
	{
		StartLine();
		_sb.Append("FileNode");
		
		const int childrenCount = 3;
		var childIndex = 0;
		
		StartLineChild(childIndex++, childrenCount);
		_sb.Append("Module name: '").Append(node.ModuleName.Text).Append('\'');
		
		StartLineChild(childIndex++, childrenCount);
		_sb.Append("Imports:");
		
		_hasMoreSiblings.Add(true);
		
		for (var i = 0; i < node.Imports.Length; i++)
		{
			var last = i == node.Imports.Length - 1;
			var import = node.Imports[i];
			VisitAction(() =>
			{
				StartLine(IsLast());
				_sb.Append(import.ModuleName.Text).Append('.');
				
				switch (import.Import)
				{
					case FullImport:
						_sb.Append('*');
						break;
					
					case TokenImport ti:
						_sb.Append(ti.Token.AsSpan());
						break;
					
					case ListImport li:
						_sb.Append('[').AppendJoin(", ", li.Tokens.Select(static t => t.GetText())).Append(']');
						break;
				}
			}, last);
		}
		
		_hasMoreSiblings.RemoveAt(_hasMoreSiblings.Count - 1);
		
		StartLineChild(childIndex, childrenCount);
		_sb.Append("Declarations:");
		
		_hasMoreSiblings.Add(false);
		
		for (var i = 0; i < node.Declarations.Length; i++)
		{
			var last = i == node.Declarations.Length - 1;
			VisitNode(node.Declarations[i], last);
		}
		
		_hasMoreSiblings.RemoveAt(_hasMoreSiblings.Count - 1);
	}
	
	public void Visit(BlockStatementNode node)
	{
		StartLine();
		_sb.Append("BlockStatement");
		
		for (var i = 0; i < node.StatementNodes.Length; i++)
		{
			var last = i == node.StatementNodes.Length - 1;
			VisitNode(node.StatementNodes[i], last);
		}
	}
	
	public void Visit(FunctionNode node)
	{
		StartLine();
		_sb.Append("FunctionNode '").Append(node.Identifier.AsSpan()).Append('\'');
		
		if (node.Parameters.Length > 0)
		{
			_sb.Append(" (");
			
			for (var i = 0; i < node.Parameters.Length; i++)
			{
				var param = node.Parameters[i];
				if (i > 0)
					_sb.Append(", ");
				
				Visit(param);
			}
			
			_sb.Append(')');
		}
		
		if (node.ReturnType is { } returnType)
			_sb.Append(" -> '").Append(returnType.AsSpan()).Append('\'');
		
		VisitNode(node.Body, true);
	}
	
	public void Visit(ParameterNode node) =>
		_sb.Append(node.Identifier.AsSpan()).Append(": ").Append(node.Type.AsSpan());
	
	public void Visit(LiteralExpressionNode node)
	{
		StartLine();
		_sb.Append("LiteralExpressionNode: ").Append(node.Token.AsSpan());
	}
	
	public void Visit(BinaryOpExpressionNode node)
	{
		StartLine();
		_sb.Append("BinaryOpExpressionNode: ").Append(node.Op.AsSpan());
		VisitNode(node.Left, false);
		VisitNode(node.Right, true);
	}
	
	public void Visit(ExpressionStatementNode node)
	{
		StartLine();
		_sb.Append("ExpressionStatementNode");
		VisitNode(node.ExpressionNode, true);
	}
	
	public void Visit(ReturnStatementNode node)
	{
		StartLine();
		_sb.Append("ReturnStatementNode");
		if (node.ExpressionNode is { } expressionNode)
			VisitNode(expressionNode, true);
	}
	
	public void Visit(UnaryOpExpressionNode node)
	{
		StartLine();
		_sb.Append("UnaryOpExpressionNode: ").Append(node.Op.AsSpan());
		VisitNode(node.Operand, true);
	}
	
	public void Visit(VarExpressionNode node)
	{
		StartLine();
		_sb.Append("VarExpressionNode: ").Append(node.Identifier.AsSpan());
	}
	
	public void Visit(VarStatementNode node)
	{
		StartLine();
		_sb.Append("VarStatementNode '").Append(node.Identifier.AsSpan()).Append('\'');
		
		if (node.Type is { } type)
			_sb.Append(" (").Append(type.AsSpan()).Append(')');
		
		if (node.ExpressionNode is { } expressionNode)
			VisitNode(expressionNode, true);
	}
	
	private bool IsLast() => _hasMoreSiblings.Count == 0 || !_hasMoreSiblings[^1];
	private void StartLine() => StartLine(IsLast());
	private void StartLineChild(int index, int total) => StartLine(index == total - 1);
}