using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed partial class Lowerer
{
	private sealed partial class FunctionLowerer
	{
		private readonly record struct LoweredArm
		(
			ImmutableArray<ResolvedPattern> Patterns,
			IResolvedExpressionNode? Guard,
			SourceLocation Location
		);
		
		private void LowerMatch(IResolvedExpressionNode value, IReadOnlyList<LoweredArm> arms, SourceLocation location,
			bool ownsValue, Action<int> lowerArm)
		{
			var scrutinee = value.Type is DynType ? LowerDynAddress(value) : LowerScrutinee(value);
			var mergeBlock = CreateBlock("match_end");
			var fallthrough = !arms.Any(static arm => arm.Patterns.IsEmpty) && _coverage.Covers(value.Type,
				arms.Where(static arm => arm.Guard is null).SelectMany(static arm => arm.Patterns))
				? CreateBlock("match_unreachable")
				: mergeBlock;
			
			var armBlocks = arms.Select(_ => CreateBlock("match_arm")).ToList();
			var nextBlocks = new BasicBlock?[arms.Count];
			GetOrMakeBlock();
			if (!LowerSwitch(scrutinee, arms, armBlocks, fallthrough, location))
			{
				for (var i = 0; i < arms.Count; i++)
				{
					var nextBlock = i == arms.Count - 1 ? fallthrough : CreateBlock("match_next");
					nextBlocks[i] = nextBlock;
					if (arms[i].Patterns.IsEmpty)
					{
						GetOrMakeBlock().SetTerminator(new BranchTerminator(armBlocks[i], location));
						break;
					}
					
					LowerPatternTests(scrutinee, arms[i].Patterns, armBlocks[i], nextBlock);
					currentBlock = nextBlock;
				}
			}
			
			for (var i = 0; i < arms.Count; i++)
			{
				var (patterns, guard, armLocation) = arms[i];
				if (!armBlocks[i].HasPredecessor())
				{
					_function.Blocks.Remove(armBlocks[i]);
					continue;
				}
				
				currentBlock = armBlocks[i];
				var scope = BeginScope(armLocation);
				if (patterns is [var single])
					BindPattern(scrutinee, single, ownsValue, ownsValue && guard is null);
				
				if (guard is not null)
				{
					var bodyBlock = CreateBlock("match_guarded");
					var failedBlock = CreateBlock("match_guard_failed");
					LowerBranch(guard, bodyBlock, failedBlock);
					if (failedBlock.HasPredecessor())
						ExitScope(failedBlock, scope, nextBlocks[i] ?? fallthrough);
					else
						_function.Blocks.Remove(failedBlock);
					
					currentBlock = bodyBlock;
				}
				
				if (ownsValue)
					ReleaseScrutinee(scrutinee, patterns is [var owned] ? owned : null, guard is not null);
				
				lowerArm(i);
				EndCurrentScope();
				currentBlock?.FillTerminator(new BranchTerminator(mergeBlock, location));
			}
			
			if (fallthrough != mergeBlock)
			{
				if (fallthrough.HasPredecessor())
					fallthrough.SetTerminator(new UnreachableTerminator(location));
				else
					_function.Blocks.Remove(fallthrough);
			}
			
			currentBlock = null;
			ContinueWith(mergeBlock);
		}
		
		private bool LowerSwitch(Value scrutinee, IReadOnlyList<LoweredArm> arms, List<BasicBlock> armBlocks,
			BasicBlock fallthrough, SourceLocation location)
		{
			const int largestRange = 256;
			Value selector;
			if (scrutinee.Type is EnumSymbol enumType)
				selector = new EnumTagValue(_typePool.GetTagType(enumType), scrutinee, location);
			else if (scrutinee.Type == NativeSymbols.Bool || scrutinee.Type is IntegerType)
				selector = scrutinee;
			else
				return false;
			
			var cases = new List<(BigInteger Value, BasicBlock Target)>();
			var seen = new HashSet<BigInteger>();
			var defaultTarget = fallthrough;
			for (var i = 0; i < arms.Count; i++)
			{
				var arm = arms[i];
				if (arm.Guard is not null)
					return false;
				
				if (arm.Patterns.IsEmpty)
				{
					defaultTarget = armBlocks[i];
					break;
				}
				
				foreach (var pattern in arm.Patterns)
				{
					IEnumerable<BigInteger> values;
					if (scrutinee.Type is EnumSymbol switched)
					{
						if (pattern is not { Case: { } enumCase, IsConditional: false })
							return false;
						
						values = [_typePool.GetCaseValue(switched, enumCase)];
					}
					else if (pattern is { Spans: [var (low, high)], Test: not null } && high - low < largestRange)
					{
						values = Enumerable.Range(0, (int)(high - low + 1)).Select(offset => low + offset);
					}
					else
					{
						return false;
					}
					
					foreach (var value in values)
					{
						if (seen.Add(value))
							cases.Add((value, armBlocks[i]));
					}
				}
			}
			
			if (cases.Count == 0)
				return false;
			
			GetOrMakeBlock().SetTerminator(new SwitchTerminator(CaptureAsAtomic(selector, "selector"), defaultTarget,
				[..cases], location));
			
			currentBlock = null;
			return true;
		}
		
		private void LowerPatternTests(Value subject, ImmutableArray<ResolvedPattern> patterns, BasicBlock success,
			BasicBlock failure)
		{
			for (var i = 0; i < patterns.Length; i++)
			{
				var next = i == patterns.Length - 1 ? failure : CreateBlock("match_alternative");
				LowerPatternTest(subject, patterns[i], success, next);
				currentBlock = next;
			}
		}
		
		private void LowerPatternTest(Value subject, ResolvedPattern pattern, BasicBlock success, BasicBlock failure)
		{
			var location = subject.SourceLocation;
			var block = GetOrMakeBlock();
			if (pattern.TestedType is not null)
			{
				var tested = pattern.IsConditional ? CreateBlock("match_fields") : success;
				block.SetTerminator(new ConditionalBranchTerminator(TestDyn(subject, pattern), tested, failure,
					location));
				
				currentBlock = null;
				if (tested == success)
					return;
				
				currentBlock = tested;
				LowerFieldTests(CastRecord(subject, pattern), pattern, success, failure);
				return;
			}
			
			if (pattern.Record is not null)
			{
				LowerFieldTests(subject, pattern, success, failure);
				return;
			}
			
			if (pattern.Test is { } test)
			{
				var tested = subject.Type is PointerType { BaseType: DynType dyn }
					? new UnaryOpValue(dyn, subject, UnaryOperation.Dereference, location)
					: subject;
				
				DeclareSubject(pattern.Subject!, tested);
				LowerBranch(test, success, failure);
				return;
			}
			
			var enumCase = pattern.Case!;
			var elements = pattern.Elements.Select(static (element, i) => (Element: element, Index: i))
				.Where(static entry => entry.Element is not null)
				.ToList();
			
			var matched = elements.Count == 0 ? success : CreateBlock("match_payload");
			block.SetTerminator(new ConditionalBranchTerminator(TestCase(subject, enumCase), matched, failure,
				location));
			
			currentBlock = matched;
			var payloadTypes = _typePool.GetPayloadTypes((EnumSymbol)subject.Type, enumCase);
			for (var i = 0; i < elements.Count; i++)
			{
				var (element, index) = elements[i];
				var next = i == elements.Count - 1 ? success : CreateBlock("match_payload");
				LowerPatternTest(new EnumPayloadValue(payloadTypes[index], subject, enumCase, index, location),
					element!,
					next, failure);
				
				currentBlock = next;
			}
			
			currentBlock = null;
		}
		
		private void LowerFieldTests(Value record, ResolvedPattern pattern, BasicBlock success, BasicBlock failure)
		{
			var elements = pattern.Elements.Select(static (element, i) => (Element: element, Index: i))
				.Where(static entry => entry.Element is { IsIrrefutable: false })
				.ToList();
			
			if (elements.Count == 0)
			{
				GetOrMakeBlock().SetTerminator(new BranchTerminator(success, record.SourceLocation));
				currentBlock = null;
				return;
			}
			
			for (var i = 0; i < elements.Count; i++)
			{
				var (element, index) = elements[i];
				var next = i == elements.Count - 1 ? success : CreateBlock("match_field");
				LowerPatternTest(GetField(record, pattern, index, record.SourceLocation), element!, next, failure);
				currentBlock = next;
			}
			
			currentBlock = null;
		}
		
		private AccessValue GetField(Value record, ResolvedPattern pattern, int index, SourceLocation location)
		{
			var field = pattern.Fields[index];
			return new AccessValue(_typePool.GetTypeOfMember(field), record, field, location);
		}
		
		private UnaryOpValue CastRecord(Value address, ResolvedPattern pattern)
		{
			var location = address.SourceLocation;
			var type = pattern.TestedType!;
			var pointer = _typePool.GetPointerType(type);
			return new UnaryOpValue(type,
				new ConversionValue(address, new DynCastConversion(address.Type, pointer), location),
				UnaryOperation.Dereference, location);
		}
		
		private void DeclareSubject(LocalVariableSymbol subject, Value value)
		{
			var location = subject.Identifier.SourceLocation;
			var initializer = subject is { IsBorrowBinding: true, Type: PointerType pointer }
				? new UnaryOpValue(pointer, value, UnaryOperation.AddressOf, location)
				: value;
			
			Declare(GetOrMakeBlock(), new LocalVarInstruction(subject, initializer, location, CurrentScopeId));
		}
		
		private Value LowerDynAddress(IResolvedExpressionNode node)
		{
			var place = LowerScrutinee(node);
			return CaptureAsAtomic(new UnaryOpValue(_typePool.GetPointerType(place.Type), place,
				UnaryOperation.AddressOf, place.SourceLocation), "dyn");
		}
		
		private static ConversionValue TestDyn(Value address, ResolvedPattern pattern) =>
			new(address, new DynTestConversion(address.Type, pattern.TestedType!), address.SourceLocation);
		
		private void BindDyn(Value address, ResolvedPattern pattern)
		{
			if (pattern.TypeBinding is not { } binding)
				return;
			
			var location = binding.Identifier.SourceLocation;
			GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(binding,
				new ConversionValue(address, new DynCastConversion(address.Type, binding.Type), location), location,
				CurrentScopeId));
		}
		
		private void LowerBranch(IResolvedExpressionNode condition, BasicBlock trueBlock, BasicBlock falseBlock)
		{
			var location = condition.Syntax.SourceLocation;
			if (_evaluator.EvaluateWithoutDrops(condition) is null)
			{
				switch (condition)
				{
					case ResolvedBinaryOpExpressionNode
					{
						Operation: NativeImpl { Op: TokenType.OpAmpersandAmpersand or TokenType.OpBarBar } operation
					} node:
					{
						var rightBlock = CreateBlock("cond_right");
						if (operation.Op == TokenType.OpAmpersandAmpersand)
							LowerBranch(node.Left, rightBlock, falseBlock);
						else
							LowerBranch(node.Left, trueBlock, rightBlock);
						
						currentBlock = rightBlock;
						LowerBranch(node.Right, trueBlock, falseBlock);
						return;
					}
					
					case ResolvedUnaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpBang } } node:
						LowerBranch(node.Operand, falseBlock, trueBlock);
						return;
					
					case ResolvedIsExpressionNode node when !IsSimpleTest(node.Pattern):
					{
						var scrutinee = node.Pattern.TestedType is not null
							? LowerDynAddress(node.Value)
							: LowerScrutinee(node.Value);
						
						var bindBlock = CreateBlock("is_bind");
						LowerPatternTest(scrutinee, node.Pattern, bindBlock, falseBlock);
						currentBlock = bindBlock;
						BindPattern(scrutinee, node.Pattern, node.OwnsValue, node.OwnsValue);
						if (node.OwnsValue)
							ReleaseScrutinee(scrutinee, node.Pattern, false);
						
						GetOrMakeBlock().SetTerminator(new BranchTerminator(trueBlock, location));
						currentBlock = null;
						return;
					}
				}
			}
			
			GetOrMakeBlock();
			var value = VisitNode(condition);
			currentBlock?.SetTerminator(new ConditionalBranchTerminator(value, trueBlock, falseBlock, location));
			currentBlock = null;
		}
		
		private Value LowerScrutinee(IResolvedExpressionNode node)
		{
			var value = VisitNode(node);
			return value is VariableValue or GlobalValue or AccessValue or IndexerValue
				or UnaryOpValue { Op: UnaryOperation.Dereference }
				? StabilizeStorage(value)
				: CaptureAsAtomic(value, "scrutinee");
		}
		
		private BinOpValue TestCase(Value scrutinee, EnumCaseSymbol enumCase)
		{
			var location = scrutinee.SourceLocation;
			var enumType = (EnumSymbol)scrutinee.Type;
			var tagType = _typePool.GetTagType(enumType);
			var tag = new EnumTagValue(tagType, scrutinee, location);
			var caseTag = MakeConstant(tagType, _typePool.GetCaseValue(enumType, enumCase));
			return new BinOpValue(NativeSymbols.Bool, tag, caseTag, BinaryOperation.Equal, location);
		}
		
		private static bool IsSimpleTest(ResolvedPattern pattern) =>
			!pattern.HasBindings && !pattern.IsConditional;
		
		private void BindPattern(Value subject, ResolvedPattern pattern, bool ownsValue, bool holdsBorrowed = false)
		{
			if (pattern.TestedType is not null)
			{
				BindDyn(subject, pattern);
				if (pattern.Record is not null)
					BindFields(CastRecord(subject, pattern), pattern, ownsValue);
				
				return;
			}
			
			if (pattern.Record is not null)
			{
				BindFields(subject, pattern, ownsValue);
				return;
			}
			
			if (pattern.Case is not { } enumCase)
				return;
			
			var payloadTypes = _typePool.GetPayloadTypes((EnumSymbol)subject.Type, enumCase);
			for (var i = 0; i < pattern.Bindings.Length; i++)
			{
				var payload = new EnumPayloadValue(payloadTypes[i], subject, enumCase, i, subject.SourceLocation);
				var element = pattern.Elements.Length > i ? pattern.Elements[i] : null;
				if (holdsBorrowed && pattern.Bindings[i] is null && IsBorrowedRecord(element))
				{
					_heldSlots.Add(element!);
					BindPattern(StoreTemporary(new MoveValue(payload), "held"), element!, false);
				}
				else if (element is { Case: not null } && pattern.Bindings[i] is null)
				{
					BindPattern(payload, element, ownsValue, holdsBorrowed);
				}
				else
				{
					BindSlot(payload, pattern.Bindings[i], element, ownsValue);
				}
			}
		}
		
		private bool IsBorrowedRecord(ResolvedPattern? element) =>
			element is { Record: not null, TestedType: null, HasBindings: true } && !MovesParts(element);
		
		private void BindFields(Value record, ResolvedPattern pattern, bool ownsValue)
		{
			for (var i = 0; i < pattern.Fields.Length; i++)
			{
				var location = pattern.Bindings[i]?.Identifier.SourceLocation ?? record.SourceLocation;
				BindSlot(GetField(record, pattern, i, location), pattern.Bindings[i], pattern.Elements[i], ownsValue);
			}
		}
		
		private void BindSlot(Value slot, LocalVariableSymbol? binding, ResolvedPattern? element, bool ownsValue)
		{
			if (element is not null)
				BindPattern(slot, element, ownsValue);
			
			if (binding is null)
				return;
			
			var location = binding.Identifier.SourceLocation;
			if (binding is { IsBorrowBinding: true, Type: PointerType pointer })
				GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(binding,
					new UnaryOpValue(pointer, slot, UnaryOperation.AddressOf, location), location, CurrentScopeId));
			else if (_typePool.NeedsDrop(slot.Type))
				Declare(GetOrMakeBlock(),
					new LocalVarInstruction(binding, new MoveValue(slot), location, CurrentScopeId));
			else
				GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(binding, slot, location, CurrentScopeId));
		}
		
		private void ReleaseScrutinee(Value scrutinee, ResolvedPattern? pattern, bool isGuarded)
		{
			if (isGuarded && pattern is not null && pattern.AllBindings.Any(static binding => binding.IsBorrowBinding))
				return;
			
			if (pattern is { Case: not null })
			{
				ReleasePayload(scrutinee, pattern);
				GetOrMakeBlock().Instructions.Add(new ExpressionInstruction(Consume(scrutinee)));
			}
			else
			{
				StoreTemporary(Consume(scrutinee), "unbound");
			}
		}
		
		private void ReleasePayload(Value subject, ResolvedPattern pattern)
		{
			var enumCase = pattern.Case!;
			var payloadTypes = _typePool.GetPayloadTypes((EnumSymbol)subject.Type, enumCase);
			for (var i = 0; i < pattern.Bindings.Length; i++)
			{
				if (pattern.Bindings[i] is not null)
					continue;
				
				var payload = new EnumPayloadValue(payloadTypes[i], subject, enumCase, i, subject.SourceLocation);
				ReleaseSlot(payload, pattern.Elements.Length > i ? pattern.Elements[i] : null);
			}
		}
		
		private void ReleaseSlot(Value slot, ResolvedPattern? element)
		{
			if (element is not null && _heldSlots.Contains(element))
				return;
			
			if (element is { Case: not null })
				ReleasePayload(slot, element);
			else if (element is { Record: not null, TestedType: null } && MovesParts(element))
				ReleaseFields(slot, element);
			else if (_typePool.NeedsDrop(slot.Type))
				StoreTemporary(new MoveValue(slot), "unbound");
		}
		
		private void ReleaseFields(Value record, ResolvedPattern pattern)
		{
			foreach (var field in _typePool.GetMembers(record.Type).OfType<FieldSymbol>())
			{
				var index = pattern.Fields.IndexOf(field);
				var value = new AccessValue(_typePool.GetTypeOfMember(field), record, field, record.SourceLocation);
				if (index < 0)
				{
					if (_typePool.NeedsDrop(value.Type))
						StoreTemporary(new MoveValue(value), "unbound");
				}
				else if (pattern.Bindings[index] is null)
				{
					ReleaseSlot(value, pattern.Elements[index]);
				}
			}
		}
		
		private bool MovesParts(ResolvedPattern pattern) =>
			pattern.AllBindings.Any(binding => !binding.IsBorrowBinding && _typePool.NeedsDrop(binding.Type));
	}
}