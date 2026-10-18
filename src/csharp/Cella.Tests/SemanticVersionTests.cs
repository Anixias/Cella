using Cella.Compiler.Projects;
using Xunit;

namespace Cella.Tests;

public sealed class SemanticVersionTests
{
	[Theory]
	[InlineData("0.1.0")]
	[InlineData("0.5.0-dev")]
	[InlineData("1.0.0-alpha.1")]
	[InlineData("10.20.30-rc-2")]
	public void ParsesValidVersions(string text) => Assert.Equal(text, SemanticVersion.Parse(text)?.ToString());
	
	[Theory]
	[InlineData("1.2")]
	[InlineData("1.2.3.4")]
	[InlineData("01.2.3")]
	[InlineData("1.2.3-")]
	[InlineData("1.2.3-a..b")]
	[InlineData("1.2.3-01")]
	[InlineData("1.2.3+build")]
	[InlineData("v1.2.3")]
	public void RejectsInvalidVersions(string text) => Assert.Null(SemanticVersion.Parse(text));
	
	[Fact]
	public void OrdersVersions()
	{
		string[] ordered =
		[
			"0.4.2", "0.5.0-dev", "0.5.0", "0.5.1", "0.10.0", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta",
			"1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"
		];
		
		var versions = ordered.Select(static text => SemanticVersion.Parse(text)!.Value).ToList();
		for (var i = 1; i < versions.Count; i++)
		{
			Assert.True(versions[i - 1].CompareTo(versions[i]) < 0, $"{ordered[i - 1]} < {ordered[i]}");
			Assert.True(versions[i].CompareTo(versions[i - 1]) > 0, $"{ordered[i]} > {ordered[i - 1]}");
		}
	}
	
	[Fact]
	public void KnowsItsOwnVersion()
	{
		var path = Path.Combine(GoldenTests.GetSourceDirectory(), "..", "..", "..", "version.txt");
		Assert.Equal(File.ReadAllText(path).Trim(), SemanticVersion.Current.ToString());
	}
}