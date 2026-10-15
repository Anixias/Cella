using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private IResolvedExpressionNode ResolveBorrowAssignment(BinaryOpExpressionNode node, IResolvedExpressionNode left,
		BorrowType borrow, bool isOwnStore)
	{
		if (IsBorrowSyntax(node.Right))
			return new ResolvedAssignmentExpressionNode(borrow, left, node.Op, VisitNode(node.Right, borrow), null,
				node, isOwnStore);
		
		var right = VisitNode(node.Right, borrow.Target);
		if (right is ResolvedUnaryOpExpressionNode
		    {
			    Operation.Op: TokenType.OpStar, Operand: { Type: BorrowType source } borrowed
		    } && (source == borrow || !borrow.IsMutable && source.Target == borrow.Target))
			right = CoerceToType(borrowed, borrow);
		else if (!borrow.IsMutable && right is not ResolvedInvalidExpressionNode)
			right = CoerceToType(right, borrow);
		else
			return new ResolvedAssignmentExpressionNode(borrow.Target, Decay(left), node.Op, right, null, node,
				isOwnStore);
		
		return new ResolvedAssignmentExpressionNode(borrow, left, node.Op, right, null, node, isOwnStore);
	}
	
	private static bool IsBorrowSyntax(IExpressionNode node) => node switch
	{
		BorrowExpressionNode => true,
		MatchExpressionNode match => match.Arms.All(static arm => IsBorrowSyntax(arm.Value)),
		_ => false
	};
	
	private IResolvedExpressionNode ResolveCompoundAssignment(BinaryOpExpressionNode node, IResolvedExpressionNode left,
		bool isOwnStore)
	{
		if (ResolveDeclaredCompound(node, left) is { } declared)
			return node.Left is OwnExpressionNode own
				? Error(node, "Cannot use 'own' on compound assignments", CurrentTargetType, own.Keyword.SourceLocation)
				: declared;
		
		return ResolveCompoundOperation(node, left) is var (right, operation)
			? new ResolvedAssignmentExpressionNode(left.Type, left, node.Op, right, operation, node, isOwnStore)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private (IResolvedExpressionNode Right, OperationImpl Operation)? ResolveCompoundOperation(
		BinaryOpExpressionNode node, IResolvedExpressionNode left)
	{
		var candidates = _operatorRegistry.GetBinaryCandidates(node.Op.Type)
			.Where(candidate => candidate.ReturnType == left.Type && candidate.ParameterTypes[0] == left.Type)
			.ToList();
		
		var rightTypes = candidates.Select(static candidate => candidate.ParameterTypes[1]).Distinct().ToList();
		var isChar = node.Right is LiteralExpressionNode { Token: { Type: TokenType.CharLiteral, Suffix: null } };
		var right = AdaptCharLiteral(VisitNode(node.Right, rightTypes.Count == 1 && !isChar ? rightTypes[0] : null),
			left.Type);
		
		if (AnyInvalid(left, right))
			return null;
		
		var args = new[] { left, right };
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
		if (resolutionSet.Count != 1)
		{
			Diagnostics.Add(DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, node.Op, right));
			return null;
		}
		
		var resolution = resolutionSet[0];
		var resolvedRight = ApplyArgumentResolution(args, resolution)[1];
		if (FindShiftRangeError(node, left.Type, resolvedRight) is { } rangeError)
		{
			Diagnostics.Add(rangeError);
			return null;
		}
		
		return (resolvedRight, (OperationImpl)resolution.Callable);
	}
	
	private IResolvedExpressionNode ResolvePropertyAssignment(BinaryOpExpressionNode node,
		ResolvedPropertyExpressionNode target, bool isOwnStore)
	{
		var member = GetMemberLocation(target.Syntax);
		if (isOwnStore)
			return RejectAssignment(node, new(DiagnosticSeverity.Error, node.Left.SourceLocation,
				"Cannot assign with 'own' except through pointers"));
		
		var reader = WithDefaultAccessor(target.Property, target.Owner, false);
		var property = WithDefaultAccessor(target.Property, target.Owner, true);
		if (property.Setter is not FunctionAccessor { Function: var setter })
			return RejectAssignment(node, new(DiagnosticSeverity.Error, target.Syntax.SourceLocation,
				"Cannot reassign read-only properties"));
		
		var read = node.Op.Type == TokenType.OpEqual ? null : (reader.Getter as FunctionAccessor)?.Function;
		if (target.Owner is DynType dyn && (!IsDynMember(setter) || read is not null && !IsDynMember(read)))
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member,
				$"Cannot use '{property.Name}' through '{dyn.Name}'"));
		
		if (FindReceiverError(setter.Kind == FunctionKind.Free, target.Receiver) is { } setterError)
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member, setterError));
		
		if (!CanAccess(target.Owner, setter))
			return RejectAssignment(node, DiagnosticReporter.ReportReadOnly(member, property.Name, setter.Visibility));
		
		var setterInfo = GetFunctionInfo(setter, target.Owner, property.TraitArguments);
		var valueType = setterInfo.Signature.GetDeclaredType(setterInfo.Signature.ParameterTypes.Length - 1);
		if (node.Op.Type == TokenType.OpEqual)
			return CallAccessor(setter, target.Owner, property.TraitArguments, target.Receiver,
				[VisitNode(node.Right, valueType)], node);
		
		if (reader.Getter is not FunctionAccessor { Function: var getter })
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member, "Cannot read write-only properties"));
		
		if (FindReceiverError(getter.Kind == FunctionKind.Free, target.Receiver) is { } getterError)
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member, getterError));
		
		if (!CanAccess(target.Owner, getter))
			return RejectAssignment(node, DiagnosticReporter.ReportWriteOnly(member, property.Name, getter.Visibility));
		
		var getterInfo = GetFunctionInfo(getter, target.Owner, reader.TraitArguments);
		var receiver = target.Receiver is { } place && (TakesMutSelf(getterInfo) || TakesMutSelf(setterInfo))
			? MakeWritable(place)
			: target.Receiver;
		
		var current = CallAccessor(getter, target.Owner, reader.TraitArguments, receiver, [], target.Syntax);
		if (current.Type == valueType && ResolveDeclaredCompound(node, current) is { } declared)
		{
			if (declared is not ResolvedFunctionCallExpressionNode { Arguments: [_, var value] } call)
				return declared;
			
			TrackFunctionUse(setterInfo, node);
			return new ResolvedPropertyAssignmentExpressionNode(getterInfo, setterInfo, receiver, node.Op, value, null,
				node)
			{
				Compound = call.Function
			};
		}
		
		if (current.Type != valueType || ResolveCompoundOperation(node, current) is not var (right, operation))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		TrackFunctionUse(setterInfo, node);
		return new ResolvedPropertyAssignmentExpressionNode(getterInfo, setterInfo, receiver, node.Op, right,
			operation, node);
	}
	
	private ResolvedInvalidExpressionNode RejectAssignment(BinaryOpExpressionNode node, Diagnostic diagnostic)
	{
		VisitNode(node.Right, null);
		return Error(node, diagnostic, CurrentTargetType);
	}
}