using System.Text;
using Cella.Core.Syntax.Nodes;

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
		_sb.Append("CallExpressionNode");
		VisitNode(node.Target, node.Arguments.Length == 0);
		
		for (var i = 0; i < node.Arguments.Length; i++)
		{
			var last = i == node.Arguments.Length - 1;
			VisitNode(node.Arguments[i], last);
		}
	}
	
	public void Visit(IndexerExpressionNode node)
	{
		StartLine();
		_sb.Append("IndexerExpressionNode");
		VisitNode(node.Target, node.Arguments.Length == 0);
		
		for (var i = 0; i < node.Arguments.Length; i++)
		{
			var last = i == node.Arguments.Length - 1;
			VisitNode(node.Arguments[i], last);
		}
	}
	
	public void Visit(AccessExpressionNode node)
	{
		StartLine();
		_sb.Append("AccessExpressionNode");
		VisitNode(node.Target, false);
		
		var member = node.Member;
		VisitAction(() =>
		{
			StartLine();
			_sb.Append(member.AsSpan());
		}, true);
	}
	
	public void Visit(ArrayExpressionNode node)
	{
		StartLine();
		_sb.Append("ArrayExpressionNode");
		
		for (var i = 0; i < node.Values.Length; i++)
		{
			var last = i == node.Values.Length - 1;
			VisitNode(node.Values[i], last);
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
	
	public void Visit(FieldNode node)
	{
		StartLine();
		_sb.Append("FieldNode '").Append(node.Identifier.AsSpan()).Append('\'');
		
		_sb.Append(" (");
		VisitNode(node.Type, false);
		_sb.Append(')');
		
		// TODO Modifiers
		
		if (node.Initializer is { } expressionNode)
			VisitNode(expressionNode, true);
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
						_sb.Append('[').AppendJoin(", ", li.Tokens.Select(static t => t.Text)).Append(']');
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
		
		// TODO Modifiers
		
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
		{
			_sb.Append(" -> ");
			VisitNode(returnType, false);
		}
		
		VisitNode(node.Body, true);
	}
	
	public void Visit(GenericTypeNode node) => _sb.Append(node.SourceLocation.GetText());
	public void Visit(IdentifierTypeNode node) => _sb.Append(node.Token.Text);
	
	public void Visit(ExternalFunctionNode node)
	{
		StartLine();
		_sb.Append("ExternalFunctionNode '").Append(node.Identifier.AsSpan()).Append('\'');
		
		// TODO Modifiers
		
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
		
		if (node.ReturnType is not { } returnType)
			return;
		
		_sb.Append(" -> ");
		VisitNode(returnType, false);
	}
	
	public void Visit(ParameterNode node)
	{
		_sb.Append(node.Identifier.AsSpan()).Append(": ");
		VisitNode(node.Type, false);
	}
	
	public void Visit(RecordNode node)
	{
		StartLine();
		_sb.Append("RecordNode '").Append(node.Identifier.AsSpan()).Append('\'');
		
		// TODO Modifiers
		
		for (var i = 0; i < node.Members.Length; i++)
			VisitNode(node.Members[i], i == node.Members.Length - 1);
	}
	
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
	
	public void Visit(IfStatementNode node)
	{
		StartLine();
		_sb.Append("IfStatementNode");
		var hasElse = node.Else is not null;
		
		VisitNode(node.Condition, false);
		VisitNode(node.Then, !hasElse);
		
		if (hasElse)
			VisitNode(node.Else!, true);
	}
	
	public void Visit(ReturnStatementNode node)
	{
		StartLine();
		_sb.Append("ReturnStatementNode");
		if (node.ExpressionNode is { } expressionNode)
			VisitNode(expressionNode, true);
	}
	
	public void Visit(BreakStatementNode node)
	{
		StartLine();
		_sb.Append("BreakStatementNode");
		if (node.ExpressionNode is { } expressionNode)
			VisitNode(expressionNode, true);
	}
	
	public void Visit(ContinueStatementNode node)
	{
		StartLine();
		_sb.Append("ContinueStatementNode");
		if (node.ExpressionNode is { } expressionNode)
			VisitNode(expressionNode, true);
	}
	
	public void Visit(UnaryOpExpressionNode node)
	{
		StartLine();
		_sb.Append("UnaryOpExpressionNode: ").Append(node.Op.AsSpan());
		VisitNode(node.Operand, true);
	}
	
	public void Visit(UndefExpressionNode node)
	{
		StartLine();
		_sb.Append("UndefExpressionNode");
		
		if (node.Type is { } type)
			VisitNode(type, true);
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
		{
			_sb.Append(" (");
			VisitNode(type, false);
			_sb.Append(')');
		}
		
		if (node.ExpressionNode is { } expressionNode)
			VisitNode(expressionNode, true);
	}
	
	public void Visit(WhileStatementNode node)
	{
		StartLine();
		_sb.Append("WhileStatementNode");
		
		if (node.Label is { } label)
			_sb.Append(" '").Append(label.Text).Append('\'');
		
		VisitNode(node.Condition, false);
		VisitNode(node.Body, true);
	}
	
	public void Visit(DoWhileStatementNode node)
	{
		StartLine();
		_sb.Append("DoWhileStatementNode");
		
		if (node.Label is { } label)
			_sb.Append(" '").Append(label.Text).Append('\'');
		
		VisitNode(node.Body, false);
		VisitNode(node.Condition, true);
	}
	
	public void Visit(LoopStatementNode node)
	{
		StartLine();
		_sb.Append("LoopStatementNode");
		
		if (node.Label is { } label)
			_sb.Append(" '").Append(label.Text).Append('\'');
		
		VisitNode(node.Body, true);
	}
	
	public void Visit(RepeatStatementNode node)
	{
		StartLine();
		_sb.Append("RepeatStatementNode");
		
		if (node.Label is { } label)
			_sb.Append(" '").Append(label.Text).Append('\'');
		
		VisitNode(node.Count, false);
		VisitNode(node.Body, true);
	}
	
	private bool IsLast() => _hasMoreSiblings.Count == 0 || !_hasMoreSiblings[^1];
	private void StartLine() => StartLine(IsLast());
	private void StartLineChild(int index, int total) => StartLine(index == total - 1);
}