using System;
using System.Collections.Generic;
using System.IO;

namespace EagleDebugTools
{
	/// <summary>
	/// Running Eagle games whose scripts can be debugged (games built with the "Script debugging" option).
	/// Each one publishes `%LOCALAPPDATA%\Eagle\Debugger\Games\Game-ProcessId.yaml` while it runs (ScriptDebuggerInfo in the engine).
	/// </summary>
	internal static class EagleGames
	{
		public static string Folder =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eagle", "Debugger", "Games");

		/// <summary>Running debuggable games. Removes files left behind by games that aren't running anymore.</summary>
		public static List<DebuggerInfoFile> GetRunning()
		{
			var games = new List<DebuggerInfoFile>();
			string folder = Folder;
			if (!Directory.Exists(folder))
				return games;

			string[] files;
			try
			{
				files = Directory.GetFiles(folder, "Game-*.yaml");
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return games;
			}

			foreach (string file in files)
			{
				DebuggerInfoFile game = DebuggerInfoFile.TryRead(file, deleteIfStale: true);
				if (game != null && game.IsGame)
					games.Add(game);
			}
			return games;
		}
	}
}
