using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace EagleDebugTools
{
	/// <summary>
	/// Attaches Visual Studio to the Eagle Editor that has the solution's Eagle project open (F5), or to a running Eagle game
	/// (Debug > Attach to Eagle Game...). If no editor has the project open, F5 starts one and attaches once it has opened the project.
	///
	/// The editor writes the port of its Mono debugger agent to `Project/Cache/Scripts/Debugger.yaml` (see EagleProject),
	/// games to `%LOCALAPPDATA%\Eagle\Debugger\Games` (see EagleGames).
	/// The bundled Mono debug adapter (MonoDebugAdapter\mono-debug.exe) is started through Visual Studio's Debug Adapter Host
	/// and connects to that port.
	/// </summary>
	internal sealed class EagleDebugger
	{
		/// <summary>Debug engine GUID registered in MonoDebugAdapter.pkgdef.</summary>
		private const string MonoDebugEngineGuid = "{C2086BEE-8FE8-462E-8D67-90F715C1B5DC}";

		// Visual Studio Debug Adapter Host's "DebugAdapterHost.Launch" command
		private const string DebugAdapterHostCmdSet = "{0DDBA113-7AC1-4C6E-A2EF-DCAC3F9E731E}";
		private const int DebugAdapterHostLaunchCmdId = 0x0101;

		private readonly EagleDebugToolsPackage m_Package;
		private readonly DTE2 m_Dte;
		private readonly IVsDebugger m_Debugger;
		private readonly IVsSolutionBuildManager m_SolutionBuildManager;
		private readonly IVsStatusbar m_Statusbar;
		private readonly IVsUIShell m_UIShell;
		private readonly EditorNotRunningInfoBar m_InfoBar;

		// Texts of the start button (F5) for an Eagle scripts project, see GetStartCommandText
		private const string AttachText = "Attach to Eagle";
		private const string StartAndAttachText = "Start Eagle and Attach";

		// Whether an editor is available for the startup project (see IsEditorAvailable), for the start button's text.
		// Visual Studio asks for the text very often, so it's refreshed by a timer rather than checked each time
		private readonly System.Windows.Threading.DispatcherTimer m_EditorStateTimer;
		private string m_EditorStateProjectPath;
		private bool m_bEditorAvailable;

		// The editor that F5 started, until it has opened the project (from then on, it's found like any editor, through Debugger.yaml).
		// While it's still opening the project, F5 waits for it instead of starting another one
		private System.Diagnostics.Process m_StartedEditor;
		private string m_StartedEditorProjectPath;

		// Kept in a field: DTE event objects stop raising events once they're garbage collected
		private readonly BuildEvents m_BuildEvents;
		private EagleProject m_AttachAfterBuild;

		// Cached result of the startup project check (QueryStatus of Debug.Start calls it very often)
		private IVsHierarchy m_CachedStartupHierarchy;
		private EagleProject m_CachedStartupEagleProject;
		private Project m_CachedStartupProject;

		public static EagleDebugger Instance { get; private set; }

		private EagleDebugger(EagleDebugToolsPackage package, DTE2 dte, IVsDebugger debugger, IVsSolutionBuildManager solutionBuildManager,
			IVsStatusbar statusbar, IVsUIShell uiShell, EditorNotRunningInfoBar infoBar)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			m_Package = package;
			m_Dte = dte;
			m_Debugger = debugger;
			m_SolutionBuildManager = solutionBuildManager;
			m_Statusbar = statusbar;
			m_UIShell = uiShell;
			m_InfoBar = infoBar;

			m_BuildEvents = m_Dte.Events.BuildEvents;
			m_BuildEvents.OnBuildDone += OnBuildDone;

			// Created on the UI thread, so it ticks on it
			m_EditorStateTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromSeconds(1),
			};
			m_EditorStateTimer.Tick += OnEditorStateTimer;
			m_EditorStateTimer.Start();
		}

		public static async Task InitializeAsync(EagleDebugToolsPackage package)
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

			var dte = (DTE2)await package.GetServiceAsync(typeof(SDTE));
			var debugger = (IVsDebugger)await package.GetServiceAsync(typeof(SVsShellDebugger));
			var solutionBuildManager = (IVsSolutionBuildManager)await package.GetServiceAsync(typeof(SVsSolutionBuildManager));
			var statusbar = (IVsStatusbar)await package.GetServiceAsync(typeof(SVsStatusbar));
			var uiShell = (IVsUIShell)await package.GetServiceAsync(typeof(SVsUIShell));
			var infoBar = await EditorNotRunningInfoBar.CreateAsync(package);

			Instance = new EagleDebugger(package, dte, debugger, solutionBuildManager, statusbar, uiShell, infoBar);
		}

		/// <summary>True when Visual Studio isn't debugging (F5 would start debugging rather than continue)</summary>
		public bool IsDesignMode()
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			var mode = new DBGMODE[1];
			return m_Debugger.GetMode(mode) == VSConstants.S_OK && (mode[0] & ~DBGMODE.DBGMODE_EncMask) == DBGMODE.DBGMODE_Design;
		}

		/// <summary>
		/// The Eagle project, if the startup project is an Eagle scripts project: a project inside an Eagle project folder
		/// whose assembly has the Eagle project's name (the editor loads `Binaries\ProjectName.dll`). Otherwise null.
		/// </summary>
		public EagleProject GetStartupEagleProject(out Project startupProject)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			startupProject = null;
			if (m_SolutionBuildManager.get_StartupProject(out IVsHierarchy hierarchy) != VSConstants.S_OK || hierarchy == null)
				return null;

			if (!ReferenceEquals(hierarchy, m_CachedStartupHierarchy))
			{
				m_CachedStartupHierarchy = hierarchy;
				m_CachedStartupEagleProject = FindScriptsProjectOwner(hierarchy, out m_CachedStartupProject);
			}

			startupProject = m_CachedStartupProject;
			return m_CachedStartupEagleProject;
		}

		/// <summary>The Eagle project of the startup project, or the one that contains the solution file, or null</summary>
		public EagleProject GetSolutionEagleProject()
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			EagleProject project = GetStartupEagleProject(out _);
			if (project == null)
			{
				string solutionPath = m_Dte.Solution?.FullName;
				if (!string.IsNullOrEmpty(solutionPath))
					project = EagleProject.FindFrom(Path.GetDirectoryName(solutionPath));
			}
			return project;
		}

		/// <summary>
		/// Text of the start button (F5) when the startup project is `project`'s scripts project:
		/// "Attach to Eagle" if an editor is available (see IsEditorAvailable), "Start Eagle and Attach" otherwise.
		/// </summary>
		public string GetStartCommandText(EagleProject project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			// Checked right away the first time (or when the startup project changes), then kept up to date by the timer
			if (!string.Equals(m_EditorStateProjectPath, project.ProjectFilePath, StringComparison.OrdinalIgnoreCase))
			{
				m_EditorStateProjectPath = project.ProjectFilePath;
				m_bEditorAvailable = IsEditorAvailable(project);
			}
			return m_bEditorAvailable ? AttachText : StartAndAttachText;
		}

		// Also runs while debugging, so that the started editor is forgotten once it has opened the project (see IsEditorAvailable)
		private void OnEditorStateTimer(object sender, EventArgs e)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			EagleProject project = GetStartupEagleProject(out _);
			if (project == null)
				return;

			bool bAvailable = IsEditorAvailable(project);
			if (bAvailable == m_bEditorAvailable && string.Equals(m_EditorStateProjectPath, project.ProjectFilePath, StringComparison.OrdinalIgnoreCase))
				return;

			m_EditorStateProjectPath = project.ProjectFilePath;
			m_bEditorAvailable = bAvailable;
			m_UIShell?.UpdateCommandUI(0); // Visual Studio asks for the button's text again
		}

		/// <summary>
		/// True if an Eagle Editor has `project` open (one editor at most can: it holds the lock of the project's Debugger.yaml),
		/// or if the editor that F5 started for it is still opening it. Editors that have other projects open don't matter.
		/// </summary>
		private bool IsEditorAvailable(EagleProject project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			if (project.GetDebuggerPort() == null)
				return IsStartedEditorRunning(project);

			// The project is open: the started editor (if it's that one) is now found like any other
			if (string.Equals(m_StartedEditorProjectPath, project.ProjectFilePath, StringComparison.OrdinalIgnoreCase))
				ForgetStartedEditor();
			return true;
		}

		private void ForgetStartedEditor()
		{
			m_StartedEditor?.Dispose();
			m_StartedEditor = null;
			m_StartedEditorProjectPath = null;
		}

		private bool IsStartedEditorRunning(EagleProject project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			if (m_StartedEditor == null)
				return false;

			bool bRunning;
			try
			{
				bRunning = !m_StartedEditor.HasExited;
			}
			catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
			{
				bRunning = false;
			}

			if (!bRunning)
			{
				ForgetStartedEditor();
				return false;
			}
			return string.Equals(m_StartedEditorProjectPath, project.ProjectFilePath, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>True if a debuggable game of `project` is running</summary>
		public static bool IsGameRunning(EagleProject project)
		{
			return EagleGames.GetRunning().Any(project.IsProjectOf);
		}

		/// <summary>
		/// Debug > Attach to Eagle Game: attaches to the running game of the solution's project if there's exactly one,
		/// otherwise lets the user pick one of the running games.
		/// </summary>
		public void AttachToGame()
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			if (!IsDesignMode())
			{
				SetStatus("Already debugging");
				return;
			}

			List<DebuggerInfoFile> games = EagleGames.GetRunning();
			if (games.Count == 0)
			{
				VsShellUtilities.ShowMessageBox(m_Package,
					"No Eagle game that can be debugged is running." + Environment.NewLine + Environment.NewLine +
					"Start the game, then try again. Games can be debugged if they were built with the \"Script debugging\" option " +
					"(Eagle Editor: File > Build the game).",
					"Attach to Eagle Game", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
				return;
			}

			EagleProject project = GetSolutionEagleProject();
			List<DebuggerInfoFile> projectGames = project == null ? games : games.Where(project.IsProjectOf).ToList();

			DebuggerInfoFile game = projectGames.Count == 1 ? projectGames[0] : GamePickerDialog.Pick(games, project);
			if (game == null || !ConfirmGameSymbols(game, project))
				return;

			m_InfoBar.Close();
			LaunchDebugAdapter(PortTarget(game.Port));
		}

		/// <summary>
		/// Breakpoints only hit where the game's scripts (and their symbols) match the source code. Warns, and asks whether to
		/// attach anyway, if the scripts changed since they were last built, or if the game was built before the scripts' last build.
		/// </summary>
		private bool ConfirmGameSymbols(DebuggerInfoFile game, EagleProject project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			if (project == null || !project.IsProjectOf(game) || string.IsNullOrEmpty(game.Executable))
				return true;

			var problems = new List<string>();
			string projectSymbols = Path.Combine(project.FolderPath, "Binaries", project.Name + ".pdb");
			string gameSymbols;
			try
			{
				gameSymbols = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(game.Executable)), project.Name + ".pdb");
			}
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
			{
				return true;
			}

			if (File.Exists(projectSymbols))
			{
				GetStartupEagleProject(out Project startupProject);
				string changedScript = startupProject != null ? FindScriptChangedAfter(startupProject, File.GetLastWriteTimeUtc(projectSymbols)) : null;
				if (changedScript != null)
					problems.Add("The scripts changed since they were last built (for example " + Path.GetFileName(changedScript) + "). Rebuild them.");
			}

			if (!File.Exists(gameSymbols))
				problems.Add("The game has no symbols for its scripts (" + gameSymbols + "), so breakpoints can't hit. Build the scripts, then the game with the \"Script debugging\" option.");
			else if (File.Exists(projectSymbols) && !FilesEqual(gameSymbols, projectSymbols))
				problems.Add("The game's scripts don't match the project's current build: the game was built before the scripts were last rebuilt. Rebuild the game.");

			if (problems.Count == 0)
				return true;

			string message = "Breakpoints may not hit, or may stop on the wrong lines:" + Environment.NewLine + Environment.NewLine +
				"- " + string.Join(Environment.NewLine + "- ", problems) + Environment.NewLine + Environment.NewLine + "Attach anyway?";
			int answer = VsShellUtilities.ShowMessageBox(m_Package, message, "Attach to Eagle Game",
				OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
			return answer == (int)VSConstants.MessageBoxResult.IDYES;
		}

		/// <summary>A .cs file of `project` modified after `timeUtc`, or null</summary>
		private static string FindScriptChangedAfter(Project project, DateTime timeUtc)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			var pending = new Stack<ProjectItems>();
			pending.Push(project.ProjectItems);
			while (pending.Count > 0)
			{
				ProjectItems items = pending.Pop();
				if (items == null)
					continue;

				foreach (ProjectItem item in items)
				{
					try
					{
						if (item.FileCount > 0)
						{
							string file = item.FileNames[1];
							if (file != null && file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
								&& File.Exists(file) && File.GetLastWriteTimeUtc(file) > timeUtc)
								return file;
						}
						pending.Push(item.ProjectItems);
					}
					catch (Exception)
					{
						// Item that isn't a file (folder, reference, ...)
					}
				}
			}
			return null;
		}

		private static bool FilesEqual(string pathA, string pathB)
		{
			try
			{
				var a = new FileInfo(pathA);
				var b = new FileInfo(pathB);
				if (a.Length != b.Length)
					return false;

				byte[] bytesA = File.ReadAllBytes(pathA);
				byte[] bytesB = File.ReadAllBytes(pathB);
				for (int i = 0; i < bytesA.Length; ++i)
				{
					if (bytesA[i] != bytesB[i])
						return false;
				}
				return true;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return true; // Can't tell, don't warn
			}
		}

		/// <summary>
		/// F5: builds the scripts (if enabled in the options) and attaches.
		/// If no editor has the project open, it starts one after the build, so that it loads the rebuilt scripts (see Attach).
		/// </summary>
		public void BuildAndAttach(EagleProject project, Project startupProject)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			// No editor has the project open, but a game of it is running: F5 could mean either, so the info bar asks
			if (!IsEditorAvailable(project) && IsGameRunning(project))
			{
				m_InfoBar.Show(project);
				return;
			}

			if (!m_Package.GeneralOptions.BuildBeforeAttaching || startupProject == null)
			{
				Attach(project);
				return;
			}

			SolutionBuild solutionBuild = m_Dte.Solution.SolutionBuild;
			if (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
			{
				SetStatus("A build is already in progress");
				return;
			}

			// Attaches in OnBuildDone. The editor reloads the rebuilt scripts by itself
			m_AttachAfterBuild = project;
			try
			{
				solutionBuild.BuildProject(solutionBuild.ActiveConfiguration.Name, startupProject.UniqueName, false);
			}
			catch (Exception ex)
			{
				m_AttachAfterBuild = null;
				ShowError("Failed to build the scripts", ex.Message);
			}
		}

		private void OnBuildDone(vsBuildScope scope, vsBuildAction action)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			EagleProject project = m_AttachAfterBuild;
			m_AttachAfterBuild = null;
			if (project == null)
				return;

			if (m_Dte.Solution.SolutionBuild.LastBuildInfo != 0)
			{
				string question = IsEditorAvailable(project) ? "There were build errors. Attach to Eagle anyway?" : "There were build errors. Start Eagle and attach anyway?";
				int answer = VsShellUtilities.ShowMessageBox(m_Package, question, "Eagle Debug Tools",
					OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
				if (answer != (int)VSConstants.MessageBoxResult.IDYES)
					return;
			}

			Attach(project);
		}

		/// <summary>
		/// Attaches to the Eagle Editor that has `project` open. If no editor has it open, starts one (see StartEditorAndAttach).
		/// </summary>
		public void Attach(EagleProject project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			if (!IsDesignMode())
			{
				SetStatus("Already debugging");
				return;
			}

			int? port = project.GetDebuggerPort();
			if (port == null)
			{
				StartEditorAndAttach(project);
				return;
			}

			m_InfoBar.Close();
			LaunchDebugAdapter(PortTarget(port.Value));
		}

		/// <summary>
		/// Starts the Eagle Editor with `project`, unless the one started before is still running (it may still be opening the project),
		/// and starts debugging right away: the debug adapter waits until the editor has opened the project, then attaches.
		/// Visual Studio is debugging meanwhile, so Stop Debugging stops waiting (the editor keeps running).
		/// </summary>
		public void StartEditorAndAttach(EagleProject project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			if (!IsDesignMode())
			{
				SetStatus("Already debugging");
				return;
			}

			m_InfoBar.Close();

			bool bStarted = false;
			if (!IsStartedEditorRunning(project))
			{
				string error = EagleEditorLauncher.Launch(project, out System.Diagnostics.Process editor);
				if (error != null)
				{
					ShowError("Couldn't start the Eagle Editor", error);
					return;
				}

				ForgetStartedEditor(); // One started for another project
				m_StartedEditor = editor;
				m_StartedEditorProjectPath = project.ProjectFilePath;
				bStarted = true;
			}

			int editorProcessId = 0;
			try
			{
				if (m_StartedEditor != null)
					editorProcessId = m_StartedEditor.Id;
			}
			catch (InvalidOperationException)
			{
				// No id: the adapter then only stops waiting when debugging is stopped
			}

			string editorName = "the Eagle Editor (" + project.Name + ")";
			string target = string.Format(CultureInfo.InvariantCulture,
				"\"debuggerInfoFile\": {0},\r\n  \"processId\": {1},\r\n  \"debuggeeName\": {2}",
				JsonString(project.DebuggerInfoPath), editorProcessId, JsonString(editorName));
			if (!LaunchDebugAdapter(target))
				return;

			SetStatus((bStarted ? "Starting the Eagle Editor with " : "Waiting for the Eagle Editor to open ") + project.Name +
				". Visual Studio attaches once the project is open; Stop Debugging (Shift+F5) stops waiting.");
		}

		/// <summary>Attach configuration target for a debuggee that is listening on `port`</summary>
		private static string PortTarget(int port)
		{
			return "\"port\": " + port.ToString(CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Starts debugging with the Mono debug adapter. `target` is the part of the attach configuration that says what to attach to
		/// (see PortTarget and StartEditorAndAttach). Returns false if it couldn't be started (an error was shown).
		/// </summary>
		private bool LaunchDebugAdapter(string target)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			try
			{
				string adapterPath = Path.Combine(
					Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
					"MonoDebugAdapter", "mono-debug.exe");

				if (!File.Exists(adapterPath))
				{
					ShowError("Mono debug adapter not found",
						"The extension is missing its debug adapter:" + Environment.NewLine + adapterPath + Environment.NewLine + Environment.NewLine +
						"Reinstall Eagle Debug Tools.");
					return false;
				}

				string launchJsonPath = WriteAttachConfig(target, m_Package.GeneralOptions.AllowPropertyEvaluation);
				string args = string.Format(CultureInfo.InvariantCulture,
					"/LaunchJson:\"{0}\" /EngineGuid:\"{1}\"", launchJsonPath, MonoDebugEngineGuid);

				m_Dte.Commands.Raise(DebugAdapterHostCmdSet, DebugAdapterHostLaunchCmdId, args, IntPtr.Zero);
				return true;
			}
			catch (Exception ex)
			{
				ShowError("Failed to attach debugger", ex.ToString());
				return false;
			}
		}

		/// <summary>
		/// Writes the launch configuration consumed by the Debug Adapter Host and the Mono debug adapter.
		/// `target`: JSON members that say what to attach to, see LaunchDebugAdapter.
		/// </summary>
		private static string WriteAttachConfig(string target, bool allowPropertyEvaluation)
		{
			string dir = Path.Combine(Path.GetTempPath(), "EagleDebugTools");
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "attach.json");

			string json = string.Format(CultureInfo.InvariantCulture,
@"{{
  ""type"": ""mono"",
  ""request"": ""attach"",
  ""address"": ""127.0.0.1"",
  {0},
  ""allowPropertyEvaluation"": {1}
}}", target, allowPropertyEvaluation ? "true" : "false");

			File.WriteAllText(path, json);
			return path;
		}

		/// <summary>`value` as a JSON string literal</summary>
		private static string JsonString(string value)
		{
			var builder = new System.Text.StringBuilder(value.Length + 2);
			builder.Append('"');
			foreach (char c in value)
			{
				switch (c)
				{
					case '"': builder.Append("\\\""); break;
					case '\\': builder.Append("\\\\"); break;
					case '\n': builder.Append("\\n"); break;
					case '\r': builder.Append("\\r"); break;
					case '\t': builder.Append("\\t"); break;
					default:
						if (c < ' ')
							builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
						else
							builder.Append(c);
						break;
				}
			}
			builder.Append('"');
			return builder.ToString();
		}

		private static EagleProject FindScriptsProjectOwner(IVsHierarchy hierarchy, out Project project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			project = null;
			try
			{
				if (hierarchy.GetProperty((uint)VSConstants.VSITEMID.Root, (int)__VSHPROPID.VSHPROPID_ExtObject, out object extObject) != VSConstants.S_OK)
					return null;

				project = extObject as Project;
				string projectFile = project?.FullName;
				if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile))
					return null;

				EagleProject eagleProject = EagleProject.FindFrom(Path.GetDirectoryName(projectFile));
				if (eagleProject == null)
					return null;

				string assemblyName = project.Properties?.Item("AssemblyName")?.Value as string;
				return string.Equals(assemblyName, eagleProject.Name, StringComparison.OrdinalIgnoreCase) ? eagleProject : null;
			}
			catch (Exception)
			{
				// Not a C# project (no AssemblyName property), project not loaded, ...
				project = null;
				return null;
			}
		}

		public void SetStatus(string text)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			m_Statusbar.SetText("Eagle Debug Tools: " + text);
		}

		public void ShowError(string title, string message)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			VsShellUtilities.ShowMessageBox(m_Package, message, title,
				OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
		}
	}
}
