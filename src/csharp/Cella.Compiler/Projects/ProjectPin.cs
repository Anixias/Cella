using CsToml;
using CsToml.Error;
using CsToml.Values;

namespace Cella.Compiler.Projects;

public static class ProjectPin
{
	public static SemanticVersion? Read(byte[] project)
	{
		TomlDocument document;
		try
		{
			document = CsTomlSerializer.Deserialize<TomlDocument>(project);
		}
		catch (CsTomlSerializeException)
		{
			return null;
		}
		
		var pin = document.RootNode["CellaVersion"u8];
		return pin.HasValue && pin.ValueType == TomlValueType.String ? SemanticVersion.Parse(pin.GetString()) : null;
	}
}