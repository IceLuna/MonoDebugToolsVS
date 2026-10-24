using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.IO;
using System.Reflection;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace EagleDebugTools
{
	/// <summary>
	/// "Debug > Attach to Eagle" command.
	///
	/// Starts the bundled Mono debug adapter (MonoDebugAdapter\mono-debug.exe) through Visual Studio's built-in
	/// Debug Adapter Host. The adapter connects to the engine's Mono debugger agent
	/// (--debugger-agent=transport=dt_socket,address=127.0.0.1:2550 in ScriptEngine::Init()).
	/// </summary>
	internal sealed class AttachDebuggerCommand
	{
		/// <summary>Must match AttachToEagleCommandId / guidEagleDebugToolsCmdSet in EagleDebugToolsPackage.vsct.</summary>
		public const int CommandId = 0x0100;
		public static readonly Guid CommandSet = new Guid("0e894a9e-1804-46d1-a396-26e0e0a6f251");

		/// <summary>Debug engine GUID registered in MonoDebugAdapter.pkgdef.</summary>
		public const string MonoDebugEngineGuid = "{C2086BEE-8FE8-462E-8D67-90F715C1B5DC}";

		// Visual Studio Debug Adapter Host's "DebugAdapterHost.Launch" command
		private const string DebugAdapterHostCmdSet = "{0DDBA113-7AC1-4C6E-A2EF-DCAC3F9E731E}";
		private const int DebugAdapterHostLaunchCmdId = 0x0101;

		private readonly AsyncPackage m_Package;

		private AttachDebuggerCommand(AsyncPackage package, OleMenuCommandService commandService)
		{
			m_Package = package ?? throw new ArgumentNullException(nameof(package));
			if (commandService == null)
				throw new ArgumentNullException(nameof(commandService));

			commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
		}

		public static AttachDebuggerCommand Instance { get; private set; }

		public static async Task InitializeAsync(AsyncPackage package)
		{
			// AddCommand requires the UI thread
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

			var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
			Instance = new AttachDebuggerCommand(package, commandService);
		}

		private void Execute(object sender, EventArgs e)
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
					return;
				}

				GeneralOptions options = EagleDebugToolsPackage.Instance.GeneralOptions;
				string launchJsonPath = WriteAttachConfig(options.ConnectionPort, options.AllowPropertyEvaluation);

				var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));
				string args = string.Format(CultureInfo.InvariantCulture,
					"/LaunchJson:\"{0}\" /EngineGuid:\"{1}\"", launchJsonPath, MonoDebugEngineGuid);

				dte.Commands.Raise(DebugAdapterHostCmdSet, DebugAdapterHostLaunchCmdId, args, IntPtr.Zero);
			}
			catch (Exception ex)
			{
				ShowError("Failed to attach debugger",
					"Make sure the Eagle editor is running with the project open." + Environment.NewLine + Environment.NewLine + ex);
			}
		}

		/// <summary>
		/// Writes the launch configuration consumed by the Debug Adapter Host and the Mono debug adapter.
		/// </summary>
		private static string WriteAttachConfig(int port, bool allowPropertyEvaluation)
		{
			string dir = Path.Combine(Path.GetTempPath(), "EagleDebugTools");
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "attach.json");

			string json = string.Format(CultureInfo.InvariantCulture,
@"{{
  ""type"": ""mono"",
  ""request"": ""attach"",
  ""address"": ""127.0.0.1"",
  ""port"": {0},
  ""allowPropertyEvaluation"": {1}
}}", port, allowPropertyEvaluation ? "true" : "false");

			File.WriteAllText(path, json);
			return path;
		}

		private void ShowError(string title, string message)
		{
			VsShellUtilities.ShowMessageBox(m_Package, message, title,
				OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
		}
	}
}
