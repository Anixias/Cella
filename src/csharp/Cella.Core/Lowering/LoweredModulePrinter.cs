using System.Text;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;

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
		
		foreach (var file in module.Files)
		{
			sb.Append('[').Append(file.Symbol.Name).Append(']').AppendLine();
			
			foreach (var type in file.Types)
			{
				switch (type)
				{
					case RecordSymbol s:
						sb.Append(s.Name).Append(':').AppendLine();
						foreach (var member in s.Members)
						{
							const int memberIndent = 2;
							sb.Append(' ', memberIndent).Append(member.Name).AppendLine();
						}
						
						break;
					
					case EnumSymbol s:
						sb.Append(s.Name).Append(':').AppendLine();
						foreach (var enumCase in s.Cases)
						{
							const int caseIndent = 2;
							sb.Append(' ', caseIndent).Append(enumCase.Name).AppendLine();
						}
						
						break;
				}
			}
			
			foreach (var global in file.Globals)
				sb.Append('@').Append(global.Symbol.Name).Append(": ").Append(global.Type.Name).AppendLine();
			
			foreach (var function in file.Functions)
				PrintFunction(sb, function);
		}
		
		return sb.ToString();
	}
	
	private static void PrintFunction(StringBuilder sb, LoweredFunction function)
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
						sb.Append(i.Symbol.Type.Name).Append(" $").Append(i.Symbol.Name).Append(" ~").Append(i.ScopeId);
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
					
					case DropInstruction i:
						sb.Append("drop ");
						PrintValue(sb, i.Value);
						if (i.Guard is { } guard)
						{
							sb.Append(" if ");
							PrintValue(sb, guard);
						}
						
						sb.AppendLine();
						break;
					
					case BeginScopeInstruction i:
						sb.AppendLine($"~begin:{i.ScopeId}");
						break;
					
					case EndScopeInstruction i:
						sb.AppendLine($"~end:{i.ScopeId}");
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
	
	private static void PrintValue(StringBuilder sb, Value value)
	{
		while (true)
		{
			switch (value)
			{
				case ConstantValue v:
					sb.Append('#').Append('[').Append(v.Type.Name).Append(']');
					
					if (v.Value is null)
						sb.Append("null");
					else
						sb.Append(v.Value);
					
					break;
				
				case PointerOffsetValue v:
					PrintValue(sb, v.Pointer);
					sb.Append(v.Op == BinaryOperation.Addition ? " + " : " - ");
					PrintValue(sb, v.Offset);
					break;
				
				case PointerDifferenceValue v:
					PrintValue(sb, v.Left);
					sb.Append(" - ");
					PrintValue(sb, v.Right);
					break;
				
				case ZeroValue v:
					sb.Append("zero[").Append(v.Type.Name).Append(']');
					break;
				
				case DefaultValue v:
					sb.Append("default[").Append(v.Type.Name).Append(']');
					break;
				
				case UndefValue v:
					sb.Append("undef[").Append(v.Type.Name).Append(']');
					break;
				
				case SizeOfValue v:
					sb.Append("sizeOf(").Append(v.Target.Name).Append(')');
					break;
				
				case VariableValue v:
					sb.Append('$').Append(v.Variable.Symbol.Name);
					break;
				
				case GlobalValue v:
					sb.Append('@').Append(v.Global.Symbol.Name);
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
				
				case BinOpValue { Op: BinaryOperation.ShiftLeft } v:
					PrintValue(sb, v.Left);
					sb.Append(" << ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.ShiftRight } v:
					PrintValue(sb, v.Left);
					sb.Append(" >> ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.RotateLeft } v:
					PrintValue(sb, v.Left);
					sb.Append(" <<< ");
					value = v.Right;
					continue;
				
				case BinOpValue { Op: BinaryOperation.RotateRight } v:
					PrintValue(sb, v.Left);
					sb.Append(" >>> ");
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
				
				case MoveValue v:
					sb.Append("move ");
					value = v.Place;
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
				
				case UnaryOpValue { Op: UnaryOperation.Dereference } v:
					sb.Append("(*");
					PrintValue(sb, v.Operand);
					sb.Append(')');
					break;
				
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
				
				case FunctionReferenceValue v:
					sb.Append(v.Function.Symbol.Name);
					break;
				
				case IndirectCallValue v:
				{
					sb.Append('(');
					PrintValue(sb, v.Target);
					sb.Append(")(");
					
					for (var i = 0; i < v.Arguments.Length; i++)
					{
						if (i > 0)
							sb.Append(", ");
						
						PrintValue(sb, v.Arguments[i]);
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
				
				case EnumValue v:
				{
					sb.Append(v.Type.Name).Append('.').Append(v.Case.Name);
					if (v.Payload.IsEmpty)
						break;
					
					sb.Append('(');
					for (var i = 0; i < v.Payload.Length; i++)
					{
						if (i > 0)
							sb.Append(", ");
						
						PrintValue(sb, v.Payload[i]);
					}
					
					sb.Append(')');
					break;
				}
				
				case EnumTagValue v:
					sb.Append("tag(");
					PrintValue(sb, v.Target);
					sb.Append(')');
					break;
				
				case EnumPayloadValue v:
					PrintValue(sb, v.Target);
					sb.Append('.').Append(v.Case.Name).Append('.').Append(v.Case.Fields[v.Index].Name);
					break;
				
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