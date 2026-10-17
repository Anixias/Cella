using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cella.Compiler.Linking;
using Cella.Compiler.Projects;
using Cella.Core.Binding;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Collections;

namespace Cella.Compiler.Building;

public sealed partial class Builder
{
	private sealed record CachedProject
	(
		string Path,
		string Fingerprint,
		AssemblyInfo? Assembly,
		ImmutableArray<ProjectCheck> Checks,
		StateSnapshot State
	);
	
	private readonly List<CachedProject> _cache = [];
	private TypePool? typePool;
	private StateSnapshot? initialState;
	
	internal TypePool? TypePool => typePool;
	
	private async Task<BuildResult> BuildProjectsAsync(
		Dictionary<ProjectInfo, ImmutableArray<ProjectInfo>> projectDependencies,
		Dictionary<ProjectInfo, SystemLinkPreference> linkPreferences, CancellationToken ct)
	{
		if (typePool is null)
		{
			typePool = new TypePool(ConversionTable.CreateNative(), new OperatorRegistry(), new SizeTable());
			initialState = typePool.CreateSnapshot();
		}
		
		var projectSymbols = new Dictionary<ProjectInfo, AssemblyInfo>();
		var succeeded = true;
		var isRebuilding = false;
		var position = 0;
		foreach (var (project, dependencies) in projectDependencies)
		{
			var fingerprint = GetFingerprint(project);
			if (!isRebuilding && position < _cache.Count && _cache[position] is var cached &&
			    cached.Path == project.FilePath && cached.Fingerprint == fingerprint)
			{
				_projects.AddRange(cached.Checks);
				if (cached.Assembly is { } reused)
					projectSymbols[project] = reused;
				else
					succeeded = false;
				
				position++;
				continue;
			}
			
			if (!isRebuilding)
			{
				_cache.RemoveRange(position, _cache.Count - position);
				(position == 0 ? initialState! : _cache[position - 1].State).Restore();
				isRebuilding = true;
			}
			
			var checksBefore = _projects.Count;
			AssemblyInfo? built = null;
			if (dependencies.All(projectSymbols.ContainsKey))
			{
				var dependencyInfo = dependencies.Select(dependency => projectSymbols[dependency]).ToList();
				var assemblyInfo = await BuildProject(project, typePool, dependencyInfo, linkPreferences[project], ct);
				if (assemblyInfo.Succeeded)
					built = assemblyInfo;
			}
			
			if (built is { } assembly)
				projectSymbols[project] = assembly;
			else
				succeeded = false;
			
			_cache.Add(new(project.FilePath, fingerprint, built, [.._projects.Skip(checksBefore)],
				typePool.CreateSnapshot()));
			
			position++;
		}
		
		return new(succeeded, [.._projects]);
	}
	
	private string GetFingerprint(ProjectInfo project)
	{
		var fingerprint = new StringBuilder();
		foreach (var path in CellaProject.FindSourceFiles(project.Directory).Prepend(project.FilePath)
			         .Order(StringComparer.Ordinal))
		{
			fingerprint.Append(path).Append('|');
			if (probe is not null && string.Equals(Path.GetFullPath(path), probe.Path,
				    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
				fingerprint.Append("probe|");
			
			if (openFiles.TryGetValue(Path.GetFullPath(path), out var text))
				fingerprint.Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
			else
			{
				var file = new FileInfo(path);
				fingerprint.Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks);
			}
			
			fingerprint.Append('\n');
		}
		
		return fingerprint.ToString();
	}
}