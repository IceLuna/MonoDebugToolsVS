using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EagleDebugTools
{
	/// <summary>
	/// File published by a running Eagle Editor or game that tells how to attach to its scripts (ScriptDebuggerInfo in the engine):
	///     Version: 1
	///     Kind: Editor            # or Game
	///     Project: MyProject
	///     Port: 2550
	///     ProcessId: 1234
	///     Executable: C:/Path/To/Eagle-Editor.exe
	/// The process keeps the file locked while it runs (the OS releases the lock if it crashes),
	/// so if the file can be locked here, it's stale. The port is never "tested" by connecting to it:
	/// Mono's debugger agent stops listening after a connection without a debugger handshake.
	/// </summary>
	internal sealed class DebuggerInfoFile
	{
		// Must match ScriptDebuggerInfo in the engine (Eagle/Script/ScriptDebuggerInfo.h)
		private const long LockOffset = 0x40000000;
		private const long LockLength = 1;
		private const int ERROR_LOCK_VIOLATION = 33;

		// The file is a flat YAML map ("Key: Value" lines), so a full YAML parser isn't needed
		private static readonly Regex s_EntryRegex = new Regex(@"^\s*([A-Za-z]+)\s*:\s*(.*?)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

		public string FilePath { get; private set; }
		public bool IsGame { get; private set; }
		public string Project { get; private set; }
		public int Port { get; private set; }
		public int ProcessId { get; private set; }
		public string Executable { get; private set; }

		/// <summary>
		/// Reads the file if a running process owns it. Returns null if the file doesn't exist, is stale or can't be read.
		/// A stale file is deleted if `deleteIfStale` is set.
		/// </summary>
		public static DebuggerInfoFile TryRead(string path, bool deleteIfStale)
		{
			if (!File.Exists(path))
				return null;

			bool bStale = false;
			try
			{
				using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					try
					{
						stream.Lock(LockOffset, LockLength);
					}
					catch (IOException ex) when ((ex.HResult & 0xFFFF) == ERROR_LOCK_VIOLATION)
					{
						// Locked: the process that wrote the file is running
						using (var reader = new StreamReader(stream, Encoding.UTF8))
							return Parse(path, reader.ReadToEnd());
					}

					// Not locked: left behind by a process that isn't running anymore
					stream.Unlock(LockOffset, LockLength);
					bStale = true;
				}
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return null;
			}

			if (bStale && deleteIfStale)
			{
				try { File.Delete(path); }
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
			}
			return null;
		}

		private static DebuggerInfoFile Parse(string path, string text)
		{
			var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (Match match in s_EntryRegex.Matches(text))
				values[match.Groups[1].Value] = Unquote(match.Groups[2].Value);

			if (!values.TryGetValue("Port", out string portText) || !int.TryParse(portText, out int port) || port <= 0 || port > ushort.MaxValue)
				return null;

			values.TryGetValue("ProcessId", out string processIdText);
			int.TryParse(processIdText, out int processId);
			values.TryGetValue("Kind", out string kind);
			values.TryGetValue("Project", out string project);
			values.TryGetValue("Executable", out string executable);

			return new DebuggerInfoFile
			{
				FilePath = path,
				IsGame = string.Equals(kind, "Game", StringComparison.OrdinalIgnoreCase),
				Project = project ?? string.Empty,
				Port = port,
				ProcessId = processId,
				Executable = executable ?? string.Empty,
			};
		}

		// yaml-cpp quotes a value only when it has to
		private static string Unquote(string value)
		{
			if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
				return value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
			if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
				return value.Substring(1, value.Length - 2).Replace("''", "'");
			return value;
		}
	}
}
