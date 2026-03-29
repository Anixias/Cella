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
		printer.VisitNode(root, true);
		return printer._sb.ToString();
	}
	
	private void StartLine(bool isLast)
	{
		// The first line is the root so should not be indented at all
		if (_sb.Length == 0)
			return;
		
		_sb.AppendLine();
		for (var i = 0; i < _hasMoreSiblings.Count - 1; i++)
			_sb.Append(_hasMoreSiblings[i] ? "│   " : "    ");
		
		if (_hasMoreSiblings.Count > 0)
			_sb.Append(isLast ? "└── " : "├── ");
	}
	
	private void VisitNode(ISyntaxNode node, bool isLast)
	{
		_hasMoreSiblings.Add(!isLast);
		((ISyntaxNodeVisitor)this).Visit(node);
		_hasMoreSiblings.RemoveAt(_hasMoreSiblings.Count - 1);
	}
	
	public void Visit(FileNode node)
	{
		StartLine();
		_sb.Append("FileNode");
		
		const int childrenCount = 2;
		var childIndex = 0;
		
		StartLineChild(childIndex++, childrenCount);
		_sb.Append("Module name: '").Append(node.ModuleIdentifier.AsSpan()).Append('\'');
		
		StartLineChild(childIndex, childrenCount);
		_sb.Append("Declarations:");
		
		for (var i = 0; i < node.Declarations.Length; i++)
		{
			var last = i == node.Declarations.Length - 1;
			VisitNode(node.Declarations[i], last);
		}
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
		
		// TODO Optional return type & type expression instead of identifier token
		_sb.Append(" -> '").Append(node.ReturnType.AsSpan()).Append('\'');
		
		VisitNode(node.Body, true);
	}
	
	public void Visit(LiteralExpressionNode node)
	{
		StartLine();
		_sb.Append("LiteralExpressionNode: ").Append(node.Token.ToString());
	}
	
	public void Visit(ReturnStatementNode node)
	{
		StartLine();
		_sb.Append("ReturnStatementNode");
		
		if (node.ExpressionNode is { } expressionNode)
			VisitNode(expressionNode, true);
	}
	
	private bool IsLast() => _hasMoreSiblings.Count == 0 || !_hasMoreSiblings[^1];
	private void StartLine() => StartLine(IsLast());
	private void StartLineChild(int index, int total) => StartLine(index == total - 1);
}