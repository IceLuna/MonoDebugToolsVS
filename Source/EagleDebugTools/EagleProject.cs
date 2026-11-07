using System;
using System.IO;

namespace EagleDebugTools
{
	/// <summary>
	/// An Eagle project: the folder that contains a `.egproj` file.
	/// </summary>
	internal sealed class EagleProject
	{
		// How many parent folders are searched for a .egproj file, starting from a project's or solution's folder
		private const int MaxSearchDepth = 5;

		/// <summary>Project name, also the name of the scripts assembly the editor loads (Binaries\Name.dll)</summary>
		public string Name { get; }

		/// <summary>Full path to the .egproj file</summary>
		public string ProjectFilePath { get; }

		public string FolderPath => Path.GetDirectoryName(ProjectFilePath);

		/// <summary>Written by the editor while it has this project open, see DebuggerInfoFile</summary>
		public string DebuggerInfoPath => Path.Combine(FolderPath, "Cache", "Scripts", "Debugger.yaml");

		private EagleProject(string projectFilePath)
		{
			ProjectFilePath = projectFilePath;
			Name = Path.GetFileNameWithoutExtension(projectFilePath);
		}

		/// <summary>
		/// Finds the Eagle project that contains `directory` (the nearest .egproj file in it or its parent folders), or null.
		/// </summary>
		public static EagleProject FindFrom(string directory)
		{
			try
			{
				var dir = string.IsNullOrEmpty(directory) ? null : new DirectoryInfo(directory);
				for (int depth = 0; dir != null && depth <= MaxSearchDepth; ++depth, dir = dir.Parent)
				{
					if (!dir.Exists)
						continue;

					FileInfo[] projectFiles = dir.GetFiles("*.egproj");
					if (projectFiles.Length > 0)
						return new EagleProject(projectFiles[0].FullName);
				}
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
			{
			}
			return null;
		}

		/// <summary>
		/// Returns the port of the script debugger of the Eagle Editor that has this project open, or null if no editor has it open.
		/// </summary>
		public int? GetDebuggerPort()
		{
			return DebuggerInfoFile.TryRead(DebuggerInfoPath, deleteIfStale: false)?.Port;
		}

		public bool IsProjectOf(DebuggerInfoFile file)
		{
			return string.Equals(file.Project, Name, StringComparison.OrdinalIgnoreCase);
		}
	}
}
