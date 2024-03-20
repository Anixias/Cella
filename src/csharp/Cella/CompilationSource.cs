using Cella.Analysis.Text;

namespace Cella;

public abstract class CompilationSource
{
	/// <summary>
	/// A source of code
	/// </summary>
	public interface IBufferSource
	{
		public Task<IBuffer> GetBuffer();
	}

	/// <summary>
	/// A collection of sources
	/// </summary>
	public interface IMultiSource
	{
		public IEnumerable<IBufferSource> GetSources();
	}
	
	public sealed class File : CompilationSource, IBufferSource
	{
		public string FilePath { get; }
		
		public File(string filePath)
		{
			FilePath = filePath;
		}

		public async Task<IBuffer> GetBuffer()
		{
			var source = await System.IO.File.ReadAllTextAsync(FilePath);
			return new StringBuffer(source);
		}
	}
	
	public sealed class Project : CompilationSource, IMultiSource
	{
		public string FilePath { get; }
		public string RootDirectory => Path.GetDirectoryName(FilePath) ?? "";
		
		public Project(string filePath)
		{
			FilePath = filePath;
		}

		public IEnumerable<IBufferSource> GetSources()
		{
			// Todo: Exclude files based on project settings
			return System.IO.Directory.EnumerateFiles(RootDirectory, "*.*", SearchOption.AllDirectories)
				.Where(IsCellaSource)
				.Select(s => new File(s));
		}

		public bool Verify(out IEnumerable<string> errors)
		{
			// Todo: Implement verification of project settings, files exist, etc.
			errors = [];
			return true;
		}
	}
	
	public sealed class Directory : CompilationSource, IMultiSource
	{
		public string FilePath { get; }
		
		public Directory(string filePath)
		{
			FilePath = filePath;
		}

		public IEnumerable<IBufferSource> GetSources()
		{
			return System.IO.Directory.EnumerateFiles(FilePath, "*.*", SearchOption.AllDirectories)
				.Where(IsCellaSource)
				.Select(s => new File(s));
		}
	}
	
	public sealed class RawText : CompilationSource, IBufferSource
	{
		private readonly string text;
		
		public RawText(string text)
		{
			this.text = text;
		}

		public Task<IBuffer> GetBuffer() => Task.FromResult<IBuffer>(new StringBuffer(text));
	}

	public static CompilationSource? FromPath(string path)
	{
		if (System.IO.Directory.Exists(path))
		{
			// If the directory contains a file named "cella", use it instead
			foreach (var file in System.IO.Directory.GetFiles(path))
			{
				if (FileIsNamedCella(file))
				{
					return new Project(file);
				}
			}
			
			// Else, return a directory
			return new Directory(path);
		}

		if (!System.IO.File.Exists(path))
			return null;
		
		// If the file is named 'cella' with no extension, it is a project file
		if (FileIsNamedCella(path))
		{
			return new Project(path);
		}

		return new File(path);

		bool FileIsNamedCella(string path)
		{
			return Path.GetFileName(path).Equals("cella", StringComparison.InvariantCultureIgnoreCase);
		}
	}

	private static bool IsCellaSource(string fileName)
	{
		return fileName.EndsWith(".ce", StringComparison.OrdinalIgnoreCase) ||
		       fileName.EndsWith(".cel", StringComparison.OrdinalIgnoreCase) ||
		       fileName.EndsWith(".cella", StringComparison.OrdinalIgnoreCase);
	}
}