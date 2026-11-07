using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EagleDebugTools
{
	/// <summary>
	/// Starts the Eagle Editor with a project. The editor is found through the .egproj file association,
	/// which the engine's Win-SetupFileAssociation.bat registers.
	/// </summary>
	internal static class EagleEditorLauncher
	{
		private const uint ASSOCF_NOTRUNCATE = 0x00000020;
		private const uint ASSOCF_INIT_IGNOREUNKNOWN = 0x00000400;
		private const int ASSOCSTR_EXECUTABLE = 2;
		private const int S_OK = 0;

		[DllImport("Shlwapi.dll", CharSet = CharSet.Unicode)]
		private static extern int AssocQueryString(uint flags, int str, string pszAssoc, string pszExtra, [Out] StringBuilder pszOut, ref uint pcchOut);

		/// <summary>Path to the program registered to open .egproj files, or null</summary>
		public static string FindEditorExecutable()
		{
			uint length = 1024;
			var buffer = new StringBuilder((int)length);
			int result = AssocQueryString(ASSOCF_INIT_IGNOREUNKNOWN | ASSOCF_NOTRUNCATE, ASSOCSTR_EXECUTABLE, ".egproj", "open", buffer, ref length);
			if (result != S_OK)
				return null;

			string path = buffer.ToString();
			return File.Exists(path) ? path : null;
		}

		/// <summary>Starts the editor with `project`. Returns an error message, or null on success</summary>
		public static string Launch(EagleProject project)
		{
			string editorPath = FindEditorExecutable();
			if (editorPath == null)
			{
				return "Couldn't find the Eagle Editor: no program is registered to open .egproj files." + Environment.NewLine + Environment.NewLine +
					"Run Win-SetupFileAssociation.bat from the Eagle engine folder (it registers the editor), then try again.";
			}

			try
			{
				Process.Start(new ProcessStartInfo(editorPath)
				{
					Arguments = "--project \"" + project.ProjectFilePath + "\"",
					WorkingDirectory = Path.GetDirectoryName(editorPath),
					UseShellExecute = false,
				});
				return null;
			}
			catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
			{
				return "Couldn't start the Eagle Editor (" + editorPath + "):" + Environment.NewLine + ex.Message;
			}
		}
	}
}
