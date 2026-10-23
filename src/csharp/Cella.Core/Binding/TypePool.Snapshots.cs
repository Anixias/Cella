using Cella.Core.Collections;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	public StateSnapshot CreateSnapshot()
	{
		var snapshot = new StateSnapshot();
		ConversionTable.Save(snapshot);
		OperatorRegistry.Save(snapshot);
		SizeTable.Save(snapshot);
		
		snapshot.SaveValue(() => TypeCompleter, value => TypeCompleter = value);
		snapshot.SaveValue(() => TraitCompleter, value => TraitCompleter = value);
		snapshot.SaveValue(() => DefaultResolver, value => DefaultResolver = value);
		snapshot.SaveValue(() => IntegerFits, value => IntegerFits = value);
		snapshot.SaveValue(() => TypeConstantValue, value => TypeConstantValue = value);
		snapshot.SaveValue(() => GlobalTypeOf, value => GlobalTypeOf = value);
		snapshot.SaveValue(() => FoldParametric, value => FoldParametric = value);
		snapshot.SaveValue(() => FindParametricFault, value => FindParametricFault = value);
		
		snapshot.Save(SizeChecks);
		snapshot.SaveNested(GenericSizeChecks);
		snapshot.SaveNested(GenericNullChecks);
		
		snapshot.SaveNested(_members);
		snapshot.Save(_arrayTypes);
		snapshot.Save(_parametricArrayTypes);
		snapshot.Save(_valueArguments);
		snapshot.Save(_pointerTypes);
		snapshot.Save(_borrowTypes);
		snapshot.Save(_dynTypes);
		snapshot.Save(_parameterDynTypes);
		snapshot.SaveNested(_traitTypes);
		snapshot.Save(_fstrTypes);
		snapshot.Save(_dynMembers);
		snapshot.Save(_dynMemberInfos);
		snapshot.Save(_functionTypes);
		snapshot.Save(_closureTypes);
		snapshot.Save(_functionDynTypes);
		snapshot.Save(_functionBounds);
		snapshot.Save(_memberTypes);
		snapshot.Save(_facts);
		snapshot.SaveNested(_constructors);
		snapshot.Save(_blockConstructors);
		snapshot.SaveNested(_explicitConversions);
		snapshot.SaveNested(_traitConstructors);
		snapshot.Save(_destructors);
		snapshot.Save(_nativeFunctions);
		snapshot.Save(_tagTypes);
		snapshot.Save(_caseValues);
		snapshot.Save(_matchedTypes);
		snapshot.Save(_matchValues);
		snapshot.SaveNested(_instances);
		snapshot.Save(_completedInstances);
		snapshot.Save(_borrowChecks);
		snapshot.SaveNested(_functionInstances);
		snapshot.Save(_genericFunctions);
		snapshot.Save(_genericGlobals);
		snapshot.Save(_globalInstances);
		snapshot.Save(_conformances);
		snapshot.SaveNested(_conformancesByTarget);
		snapshot.Save(_arrayConformances);
		snapshot.Save(_pointerConformances);
		snapshot.Save(_blanketConformances);
		snapshot.Save(_constantProjections);
		snapshot.Save(_blanketBoundsInProgress);
		snapshot.Save(_blanketLookupsInProgress);
		snapshot.Save(_bounds);
		snapshot.Save(_parameterBounds);
		snapshot.Save(_constructorBounds);
		snapshot.Save(_requiredTraits);
		snapshot.Save(_memberBlocks);
		snapshot.SaveNested(_atomicRequirements);
		snapshot.SaveNested(_typeConstantUses);
		return snapshot;
	}
}