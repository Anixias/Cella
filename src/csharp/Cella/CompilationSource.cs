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
		
		public Project(string filePath)
		{
			FilePath = filePath;
		}

		public IEnumerable<IBufferSource> GetSources()
		{
			throw new NotImplementedException();
		}

		public bool Verify(out IEnumerable<string> errors)
		{
			// Todo: Implement verification of project settings, files exist, etc.
			errors = [];
			return true;
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
		if (Directory.Exists(path))
		{
			// Todo: Return a Directory source
		}

		if (System.IO.File.Exists(path))
		{
			// If the file is named 'cella' with no extension, it is a project file
			if (Path.GetFileName(path).Equals("cella", StringComparison.InvariantCultureIgnoreCase))
			{
				
			}
		}

		return null;
	}
}