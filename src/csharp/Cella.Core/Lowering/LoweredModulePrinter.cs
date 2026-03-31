using System.Text;

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
			sb.Append(function.Symbol.Name).Append(':').AppendLine();
			foreach (var block in function.Blocks)
			{
				const int labelIndent = 2;
				sb.Append(' ', labelIndent).Append(block.Label).Append(':').AppendLine();
				
				const int instructionIndent = 4;
				foreach (var instruction in block.Instructions)
				{
					sb.Append(' ', instructionIndent);
					sb.AppendLine("[??]");
					// TODO Print instructions
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
					sb.Append('$').Append(v.Variable.Name);
					break;
				
				case TemporaryValue v:
					sb.Append('t').Append(v.Id);
					break;
				
				case ConstAddValue v:
					PrintValue(sb, v.Left);
					sb.Append(" + ");
					value = v.Right;
					continue;
				
				case ConstSubValue v:
					PrintValue(sb, v.Left);
					sb.Append(" - ");
					value = v.Right;
					continue;
				
				case ConstMulValue v:
					PrintValue(sb, v.Left);
					sb.Append(" * ");
					value = v.Right;
					continue;
				
				case ConstNegValue v:
					sb.Append('-');
					value = v.Operand;
					continue;
			}
			
			break;
		}
	}
}