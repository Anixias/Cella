using System.Collections.Immutable;
using System.Text;
using Cella.Core.Symbols;
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
	
	public void Visit(ConstructorNode node)
	{
		StartLine();
		_sb.Append("ConstructorNode");
		
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
		
		if (node.Body is { } body)
			VisitNode(body, true);
	}
	
	public void Visit(DestructorNode node)
	{
		StartLine();
		_sb.Append("DestructorNode");
		VisitNode(node.Body, true);
	}
	
	public void Visit(GlobalNode node)
	{
		StartLine();
		_sb.Append("GlobalNode '").Append(node.Identifier.AsSpan()).Append("' ").Append(node.Keyword.AsSpan());
		_sb.Append(" (");
		VisitNode(node.Type, false);
		_sb.Append(')');
		VisitNode(node.Initializer, true);
	}
	
	public void Visit(FieldNode node)
	{
		StartLine();
		_sb.Append("FieldNode '").Append(node.Identifier.AsSpan()).Append('\'');
		
		_sb.Append(" (");
		VisitNode(node.Type, false);
		_sb.Append(')');
		
		// TODO Modifiers
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
				if (import.ModuleName.Text.Length > 0)
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
		_sb.Append("FunctionNode '").Append(node.Identifier.AsSpan());
		AppendTypeParameters(node.TypeParameters);
		_sb.Append('\'');
		
		// TODO Modifiers
		
		if (node.Receiver is not null || node.Parameters.Length > 0)
		{
			_sb.Append(" (");
			
			if (node.Receiver is { } receiver)
			{
				if (receiver.Mode is { } mode)
					_sb.Append(mode.AsSpan()).Append(' ');
				
				_sb.Append(receiver.Self.AsSpan());
			}
			
			for (var i = 0; i < node.Parameters.Length; i++)
			{
				var param = node.Parameters[i];
				if (i > 0 || node.Receiver is not null)
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
		
		if (node.Body is { } body)
			VisitNode(body, true);
	}
	
	public void Visit(PropertyNode node)
	{
		StartLine();
		_sb.Append("PropertyNode '").Append(node.Identifier.AsSpan()).Append('\'');
		
		if (node.Type is { } type)
		{
			_sb.Append(" (");
			VisitNode(type, false);
			_sb.Append(')');
		}
		
		for (var i = 0; i < node.Accessors.Length; i++)
			VisitNode(node.Accessors[i], i == node.Accessors.Length - 1);
	}
	
	public void Visit(GenericTypeNode node) => _sb.Append(node.SourceLocation.GetText());
	
	public void Visit(FunctionTypeNode node) => _sb.Append(node.SourceLocation.GetText());
	
	public void Visit(BorrowTypeNode node) => _sb.Append(node.SourceLocation.GetText());
	
	public void Visit(DynTypeNode node) => _sb.Append(node.SourceLocation.GetText());
	
	public void Visit(IdentifierTypeNode node) => _sb.Append(node.Token.Text);
	
	public void Visit(QualifiedTypeNode node) => _sb.Append(node.SourceLocation.GetText());
	
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
			
			if (node.IsVariadic)
				_sb.Append(", ...");
			
			_sb.Append(')');
		}
		
		if (node.ReturnType is not { } returnType)
			return;
		
		_sb.Append(" -> ");
		VisitNode(returnType, false);
	}
	
	public void Visit(ParameterNode node)
	{
		if (node.Mode is { } mode)
			_sb.Append(mode.AsSpan()).Append(' ');
		
		_sb.Append(node.Identifier.AsSpan()).Append(": ");
		VisitNode(node.Type, false);
	}
	
	public void Visit(ConstructorConstraintNode node) => _sb.Append(node.SourceLocation.GetText());
	
	public void Visit(NativeConstructorNode node) => throw new InvalidOperationException();
	
	private void AppendTypeParameters(ImmutableArray<TypeParameterNode> typeParameters)
	{
		if (!typeParameters.IsEmpty)
			_sb.Append('[').AppendJoin(", ", typeParameters.Select(DescribeTypeParameter)).Append(']');
	}
	
	private static string DescribeTypeParameter(TypeParameterNode parameter)
	{
		var constraints = parameter.Keywords.Select(static keyword => keyword.Text)
			.Concat(parameter.Traits.Select(static trait => trait.SourceLocation.GetText().ToString()))
			.Concat(parameter.Constructors.Select(static constructor =>
				constructor.SourceLocation.GetText().ToString()))
			.ToList();
		
		return constraints.Count == 0
			? parameter.Identifier.Text
			: $"{parameter.Identifier.Text}: {string.Join(" + ", constraints)}";
	}
	
	public void Visit(TraitNode node)
	{
		StartLine();
		_sb.Append("TraitNode '").Append(node.Identifier.AsSpan());
		AppendTypeParameters(node.TypeParameters);
		_sb.Append('\'');
		if (!node.RequiredTraits.IsEmpty)
			_sb.Append(" req ").AppendJoin(" + ",
				node.RequiredTraits.Select(static trait => trait.SourceLocation.GetText().ToString()));
		
		for (var i = 0; i < node.Members.Length; i++)
			VisitNode(node.Members[i], i == node.Members.Length - 1);
	}
	
	public void Visit(ImplNode node)
	{
		StartLine();
		_sb.Append("ImplNode '").Append(node.Target.SourceLocation.GetText());
		AppendTypeParameters(node.TypeParameters);
		_sb.Append('\'');
		if (!node.Traits.IsEmpty)
			_sb.Append(' ').AppendJoin(" + ",
				node.Traits.Select(static trait => trait.SourceLocation.GetText().ToString()));
		
		for (var i = 0; i < node.Members.Length; i++)
			VisitNode(node.Members[i], i == node.Members.Length - 1);
	}
	
	public void Visit(RecordNode node)
	{
		StartLine();
		_sb.Append("RecordNode '").Append(node.Identifier.AsSpan());
		AppendTypeParameters(node.TypeParameters);
		_sb.Append('\'');
		
		// TODO Modifiers
		
		for (var i = 0; i < node.Members.Length; i++)
			VisitNode(node.Members[i], i == node.Members.Length - 1);
	}
	
	public void Visit(EnumNode node)
	{
		StartLine();
		_sb.Append(node.IsExternal ? "ExternalEnumNode '" : "EnumNode '").Append(node.Identifier.AsSpan());
		AppendTypeParameters(node.TypeParameters);
		_sb.Append('\'');
		
		if ((node.TagType ?? node.MatchedType) is { } tagType)
			VisitNode(tagType, node.Cases.IsEmpty && node.Members.IsEmpty);
		
		for (var i = 0; i < node.Cases.Length; i++)
		{
			var enumCase = node.Cases[i];
			VisitAction(() =>
			{
				StartLine();
				_sb.Append("Case '").Append(enumCase.Identifier.AsSpan()).Append('\'');
				if (enumCase.Else is not null)
					_sb.Append(" else");
				
				foreach (var field in enumCase.Payload)
					VisitNode(field, field == enumCase.Payload[^1] && enumCase.Values.IsEmpty);
				
				foreach (var value in enumCase.Values)
					VisitNode(value, value == enumCase.Values[^1]);
			}, i == node.Cases.Length - 1 && node.Members.IsEmpty);
		}
		
		for (var i = 0; i < node.Members.Length; i++)
			VisitNode(node.Members[i], i == node.Members.Length - 1);
	}
	
	public void Visit(MatchStatementNode node)
	{
		StartLine();
		_sb.Append("MatchStatementNode");
		if (node.Mode is { } mode)
			_sb.Append(' ').Append(mode.AsSpan());
		
		VisitNode(node.Value, node.Arms.Length == 0);
		
		for (var i = 0; i < node.Arms.Length; i++)
		{
			var arm = node.Arms[i];
			VisitAction(() =>
			{
				StartLine();
				_sb.Append("Arm: ").Append(arm.SourceLocation.GetText());
				VisitNode(arm.Body, true);
			}, i == node.Arms.Length - 1);
		}
	}
	
	public void Visit(MatchExpressionNode node)
	{
		StartLine();
		_sb.Append("MatchExpressionNode");
		if (node.Mode is { } mode)
			_sb.Append(' ').Append(mode.AsSpan());
		
		VisitNode(node.Value, node.Arms.Length == 0);
		
		for (var i = 0; i < node.Arms.Length; i++)
		{
			var arm = node.Arms[i];
			VisitAction(() =>
			{
				StartLine();
				_sb.Append("Arm: ").Append(arm.SourceLocation.GetText());
				VisitNode(arm.Value, true);
			}, i == node.Arms.Length - 1);
		}
	}
	
	public void Visit(IsExpressionNode node)
	{
		StartLine();
		_sb.Append("IsExpressionNode");
		if (node.Mode is { } mode)
			_sb.Append(' ').Append(mode.AsSpan());
		
		_sb.Append(": ").Append(node.Pattern.SourceLocation.GetText());
		VisitNode(node.Value, true);
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
	
	public void Visit(DropStatementNode node)
	{
		StartLine();
		_sb.Append("DropStatementNode");
		VisitNode(node.Target, true);
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
	
	public void Visit(ReturnExpressionNode node)
	{
		StartLine();
		_sb.Append("ReturnExpressionNode");
		if (node.Value is { } value)
			VisitNode(value, true);
	}
	
	public void Visit(BreakExpressionNode node)
	{
		StartLine();
		_sb.Append("BreakExpressionNode");
		if (node.Label is { } label)
			VisitNode(label, true);
	}
	
	public void Visit(ContinueExpressionNode node)
	{
		StartLine();
		_sb.Append("ContinueExpressionNode");
		if (node.Label is { } label)
			VisitNode(label, true);
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
	
	public void Visit(TypeExpressionNode node)
	{
		StartLine();
		_sb.Append("TypeExpressionNode");
		VisitNode(node.Type, true);
	}
	
	public void Visit(SizeOfExpressionNode node)
	{
		StartLine();
		_sb.Append("SizeOfExpressionNode");
		VisitNode(node.Target, true);
	}
	
	public void Visit(AlignOfExpressionNode node)
	{
		StartLine();
		_sb.Append("AlignOfExpressionNode");
		VisitNode(node.Target, true);
	}
	
	public void Visit(InterpolatedStringExpressionNode node)
	{
		StartLine();
		_sb.Append("InterpolatedStringExpressionNode");
		
		for (var i = 0; i < node.Values.Length; i++)
			VisitNode(node.Values[i], i == node.Values.Length - 1);
	}
	
	public void Visit(NameOfExpressionNode node)
	{
		StartLine();
		_sb.Append("NameOfExpressionNode: ").Append(node.Name.SourceLocation.GetText());
	}
	
	public void Visit(BorrowExpressionNode node)
	{
		StartLine();
		_sb.Append("BorrowExpressionNode: ").Append(node.Keyword.Text);
		VisitNode(node.Value, true);
	}
	
	public void Visit(OwnExpressionNode node)
	{
		StartLine();
		_sb.Append("OwnExpressionNode");
		VisitNode(node.Value, true);
	}
	
	public void Visit(AtomicExpressionNode node)
	{
		StartLine();
		_sb.Append("AtomicExpressionNode: ").Append(node.Ordering);
		if (node.Op is { } op)
			_sb.Append(' ').Append(op.Text);
		
		IExpressionNode[] parts = [..new[] { node.Place, node.Expected, node.Value }.OfType<IExpressionNode>()];
		for (var i = 0; i < parts.Length; i++)
			VisitNode(parts[i], i == parts.Length - 1);
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
	
	public void Visit(ForStatementNode node)
	{
		StartLine();
		_sb.Append("ForStatementNode '").Append(node.Binding.Text).Append('\'');
		
		if (node.Label is { } label)
			_sb.Append(" '").Append(label.Text).Append('\'');
		
		if (node.Mode is { } mode)
			_sb.Append(' ').Append(mode.Text);
		
		if (node.RangeOperator is { } rangeOperator)
			_sb.Append(' ').Append(rangeOperator.Text);
		
		VisitNode(node.Source, false);
		if (node.End is { } end)
			VisitNode(end, false);
		
		VisitNode(node.Body, true);
	}
	
	private bool IsLast() => _hasMoreSiblings.Count == 0 || !_hasMoreSiblings[^1];
	private void StartLine() => StartLine(IsLast());
	private void StartLineChild(int index, int total) => StartLine(index == total - 1);
}