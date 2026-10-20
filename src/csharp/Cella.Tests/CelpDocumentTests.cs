using System.Collections.Immutable;
using Cella.Compiler.Projects;
using Xunit;

namespace Cella.Tests;

public sealed class CelpDocumentTests
{
	private static readonly ImmutableArray<CelpKey> _topLevel = [];
	
	private static readonly ImmutableArray<CelpKey> _release =
		CelpDocument.PathOf("When", "release");
	
	[Fact]
	public void ReplacesValuesInPlace()
	{
		var document = CelpDocument.Parse("OutputType = \"Executable\" # main program\nBoundsChecks = true\n");
		Assert.Equal("OutputType = \"StaticLibrary\" # main program\nBoundsChecks = true\n",
			document.Set(_topLevel, "OutputType", "StaticLibrary").Text);
	}
	
	[Fact]
	public void AddsTopLevelSettingsBeforeTables()
	{
		var document = CelpDocument.Parse("OutputType = \"Executable\"\n\n[When.release]\nBoundsChecks = false\n");
		Assert.Equal("OutputType = \"Executable\"\nDebugInfo = false\n\n[When.release]\nBoundsChecks = false\n",
			document.Set(_topLevel, "DebugInfo", false).Text);
	}
	
	[Fact]
	public void AddsToExistingTables()
	{
		var document = CelpDocument.Parse(
			"OutputType = \"Executable\"\n\n[When.release]\nBoundsChecks = false\n\n[When.debug]\nDebugInfo = true\n");
		
		Assert.Equal(
			"OutputType = \"Executable\"\n\n[When.release]\nBoundsChecks = false\nOverflowChecks = true\n\n" +
			"[When.debug]\nDebugInfo = true\n",
			document.Set(_release, "OverflowChecks", true).Text);
	}
	
	[Fact]
	public void AddsMissingTablesAtTheEnd()
	{
		var document = CelpDocument.Parse("OutputType = \"Executable\"");
		Assert.Equal("OutputType = \"Executable\"\n\n[When.\"windows && debug\"]\nBoundsChecks = false\n",
			document.Set(CelpDocument.PathOf("When", "windows && debug"), "BoundsChecks", false).Text);
	}
	
	[Fact]
	public void FollowsDottedKeys()
	{
		var document = CelpDocument.Parse("OutputType = \"Executable\"\nWhen.release.BoundsChecks = false\n");
		Assert.Equal(
			"OutputType = \"Executable\"\nWhen.release.BoundsChecks = false\nWhen.release.DebugInfo = false\n",
			document.Set(_release, "DebugInfo", false).Text);
	}
	
	[Fact]
	public void RemovesSettings()
	{
		var document = CelpDocument.Parse("OutputType = \"Executable\"\nDebugInfo = false\nBoundsChecks = true\n");
		Assert.Equal("OutputType = \"Executable\"\nBoundsChecks = true\n",
			document.Remove(_topLevel, "DebugInfo").Text);
	}
	
	[Fact]
	public void RemovesConditions()
	{
		const string text = "OutputType = \"Executable\"\n\n[When.release]\nBoundsChecks = false\n\n" +
		                    "[[When.release.ProjectReferences]]\nPath = 'a/a.celp'\n\n[When.debug]\nDebugInfo = true\n";
		
		var document = CelpDocument.Parse(text);
		Assert.Equal("OutputType = \"Executable\"\n\n[When.debug]\nDebugInfo = true\n",
			document.RemoveTable(_release).Text);
		
		Assert.Equal(
			"OutputType = \"Executable\"\n\n[When.release]\nBoundsChecks = false\n\n" +
			"[[When.release.ProjectReferences]]\nPath = 'a/a.celp'\n",
			document.RemoveTable(CelpDocument.PathOf("When", "debug")).Text);
	}
	
	[Fact]
	public void AddsAndRemovesReferences()
	{
		var document = CelpDocument.Parse(
			"OutputType = \"Executable\"\n\n[[ProjectReferences]]\nPath = 'a/a.celp'\n\n[When.release]\nDebugInfo = false\n");
		
		var added = document.AddTable(_topLevel, "ProjectReferences", [("Path", "b/b.celp")]);
		Assert.Equal(
			"OutputType = \"Executable\"\n\n[[ProjectReferences]]\nPath = 'a/a.celp'\n\n" +
			"[[ProjectReferences]]\nPath = 'b/b.celp'\n\n[When.release]\nDebugInfo = false\n",
			added.Text);
		
		Assert.Equal(new[] { "a/a.celp", "b/b.celp" },
			added.GetReferences(_topLevel).Select(static reference => reference.Path));
		
		var removed = added.RemoveTable([new CelpKey("ProjectReferences", 0)]);
		Assert.Equal(
			"OutputType = \"Executable\"\n\n[[ProjectReferences]]\nPath = 'b/b.celp'\n\n[When.release]\nDebugInfo = false\n",
			removed.Text);
		
		var aliased = removed.Set([new CelpKey("ProjectReferences", 0)], "Alias", "bee");
		Assert.Equal("bee", Assert.Single(aliased.GetReferences(_topLevel)).Alias);
	}
	
	[Fact]
	public void KeepsWindowsLineEndings()
	{
		var document = CelpDocument.Parse("OutputType = \"Executable\"\r\n");
		Assert.Equal("OutputType = \"Executable\"\r\n\r\n[When.release]\r\nDebugInfo = false\r\n",
			document.Set(_release, "DebugInfo", false).Text);
	}
	
	[Fact]
	public void RefusesInlineTables()
	{
		var document = CelpDocument.Parse("OutputType = \"Executable\"\nWhen = { release = { DebugInfo = false } }\n");
		Assert.Throws<CelpEditException>(() => document.Set(_release, "BoundsChecks", false));
		Assert.Throws<CelpEditException>(() => document.RemoveTable(_release));
	}
	
	[Fact]
	public void ReadsValuesAndConditions()
	{
		var document = CelpDocument.Parse(
			"OutputType = \"Executable\"\nImports = [\"std.io.*\"]\n\n[Flags]\nfast = true\n\n[When.release]\nDebugInfo = false\n");
		
		Assert.Equal("Executable", document.GetValue(CelpDocument.PathOf("OutputType")));
		Assert.Equal(new object?[] { "std.io.*" },
			Assert.IsType<List<object?>>(document.GetValue(CelpDocument.PathOf("Imports"))));
		
		Assert.Equal(new[] { "fast" }, document.GetKeys(CelpDocument.PathOf("Flags")));
		Assert.Equal(new[] { "release" }, document.Conditions);
		Assert.Equal(false, document.GetValue(_release.Add(new CelpKey("DebugInfo"))));
	}
}