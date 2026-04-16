using System.Text;
using Cella.Core.Binding.Operations;

namespace Cella.Core.Lowering;

public static class LoweredModulePrinter
{
	public static string Print(LoweredModule module)
	{
		var sb = new StringBuilder();
		
		const int moduleHeaderSize = 30;
		sb.Append('=', moduleHeaderSize).AppendLine();
		
		var moduleName = module.Symbol.Name;
		var moduleNamePadding = moduleHeaderSize - 2 - moduleName.Length;
		
		sb.Append(' ', moduleNamePadding / 2).Append(moduleName).Append(' ', (moduleNamePadding + 1) / 2).AppendLine();
		sb.Append('=', moduleHeaderSize).AppendLine();
		
		foreach (var function in module.Functions)
		{
			sb.Append(function.Info.Symbol.Name).Append(':').AppendLine();
			foreach (var block in function.Blocks)
			{
				const int labelIndent = 2;
				sb.Append(' ', labelIndent).Append(block.Label).Append(':').AppendLine();
				
				const int instructionIndent = 4;
				foreach (var instruction in block.Instructions)
				{
					sb.Append(' ', instructionIndent);
					
					switch (instruction)
					{
						case LocalVarInstruction i:
							sb.Append(i.Symbol.Type.Name).Append(" $").Append(i.Symbol.Name);
							if (i.Initializer is { } initializer)
							{
								sb.Append(" = ");
								PrintValue(sb, initializer);
							}
							
							sb.AppendLine();
							break;
						
						case ExpressionInstruction i:
							PrintValue(sb, i.Value);
							sb.AppendLine();
							break;
						
						default:
							sb.AppendLine("[??]");
							break;
					}
				}
				
				sb.Append(' ', instructionIndent);
				switch (block.Terminator)
				{
					case UndefinedTerminator:
						sb.AppendLine("<ERR>");
						break;
					
					case ReturnTerminator returnTerminator:
						sb.Append("ret");
						
						if (returnTerminator.Value is { } returnValue)
						{
							sb.Append(' ');
							PrintValue(sb, returnValue);
						}
						
						sb.AppendLine();
						break;
					
					case BranchTerminator branchTerminator:
						sb.Append("jmp ").AppendLine(branchTerminator.Target.Label);
						break;
					
					case ConditionalBranchTerminator conditionalBranchTerminator:
						sb.Append("if ");
						PrintValue(sb, conditionalBranchTerminator.Condition);
						
						sb.Append(" then ")
							.Append(conditionalBranchTerminator.TrueTarget.Label)
							.Append(" else ")
							.Append(conditionalBranchTerminator.FalseTarget.Label)
							.AppendLine();
						
						break;
				}
			}
		}
		
		return sb.ToString();
	}
	
	private static void PrintValue(StringBuilder sb, Value value)
	{
		while (true)
		{
			switch (value)
			{
				case ConstantValue v:
					sb.Append('#').Append(v.Value);
					break;
				
				case VariableValue v:
					sb.Append('$').Append(v.Variable.Symbol.Name);
					break;
				
				case BinOpValue { Op: BinaryOperation.Addition } v:
					PrintValue(sb, v.Left);
					sb.Append(" + ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.Subtraction } v:
					PrintValue(sb, v.Left);
					sb.Append(" - ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.Multiplication } v:
					PrintValue(sb, v.Left);
					sb.Append(" * ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.Division } v:
					PrintValue(sb, v.Left);
					sb.Append(" / ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.Modulo } v:
					PrintValue(sb, v.Left);
					sb.Append(" % ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.BitwiseAnd } v:
					PrintValue(sb, v.Left);
					sb.Append(" & ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.BitwiseOr } v:
					PrintValue(sb, v.Left);
					sb.Append(" | ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.BitwiseXor } v:
					PrintValue(sb, v.Left);
					sb.Append(" ^ ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.LogicalAnd } v:
					PrintValue(sb, v.Left);
					sb.Append(" && ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.LogicalOr } v:
					PrintValue(sb, v.Left);
					sb.Append(" || ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.Greater } v:
					PrintValue(sb, v.Left);
					sb.Append(" > ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.GreaterEqual } v:
					PrintValue(sb, v.Left);
					sb.Append(" >= ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.Less } v:
					PrintValue(sb, v.Left);
					sb.Append(" < ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.LessEqual } v:
					PrintValue(sb, v.Left);
					sb.Append(" <= ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.Equal } v:
					PrintValue(sb, v.Left);
					sb.Append(" == ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.NotEqual } v:
					PrintValue(sb, v.Left);
					sb.Append(" != ");
					value = v.Right;
					continue;
				
				case AssignValue v:
					PrintValue(sb, v.Left);
					sb.Append(" = ");
					value = v.Right;
					continue;
				
				case ConversionValue v:
					sb.Append(v.Type.Name).Append('(');
					PrintValue(sb, v.Source);
					sb.Append(')');
					break;
				
				case UnaryOpValue { Op: UnaryOperation.Identity } v:
					sb.Append('+');
					value = v.Operand;
					continue;
				
				case UnaryOpValue { Op: UnaryOperation.Negation } v:
					sb.Append('-');
					value = v.Operand;
					continue;
				
				case UnaryOpValue { Op: UnaryOperation.BitwiseNot } v:
					sb.Append('~');
					value = v.Operand;
					continue;
				
				case UnaryOpValue { Op: UnaryOperation.LogicalNot } v:
					sb.Append('!');
					value = v.Operand;
					continue;
				
				case UnaryOpValue { Op: UnaryOperation.AddressOf } v:
					sb.Append('@');
					value = v.Operand;
					continue;
				
				case CallValue v:
				{
					sb.Append(v.Function.Symbol.Name).Append('(');
					
					var firstArg = true;
					foreach (var arg in v.Arguments)
					{
						if (firstArg)
							firstArg = false;
						else
							sb.Append(", ");
						
						PrintValue(sb, arg);
					}
					
					sb.Append(')');
					break;
				}
				
				case IndexerValue v:
				{
					PrintValue(sb, v.Target);
					sb.Append('[');
					PrintValue(sb, v.Index);
					
					// TODO Multi-dimensional?
					/*var firstArg = true;
					foreach (var arg in v.Index)
					{
						if (firstArg)
							firstArg = false;
						else
							sb.Append(", ");
						
						PrintValue(sb, arg);
					}*/
					
					sb.Append(']');
					break;
				}
				
				case AccessValue v:
				{
					PrintValue(sb, v.Target);
					sb.Append('.').Append(v.Member.Name);
					break;
				}
				
				case ArrayValue v:
				{
					sb.Append('[');
					for (var i = 0; i < v.Elements.Length; i++)
					{
						if (i > 0)
							sb.Append(", ");
						
						PrintValue(sb, v.Elements[i]);
					}
					
					sb.Append(']');
					break;
				}
			}
			
			break;
		}
	}
}