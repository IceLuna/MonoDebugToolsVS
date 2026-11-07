using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace EagleDebugTools
{
	/// <summary>
	/// "Debug > Attach to Eagle" command (Ctrl+Shift+F7): attaches without building.
	/// F5 (see StartDebuggingInterceptor) builds first.
	/// </summary>
	internal sealed class AttachDebuggerCommand
	{
		/// <summary>Must match AttachToEagleCommandId / guidEagleDebugToolsCmdSet in EagleDebugToolsPackage.vsct.</summary>
		public const int CommandId = 0x0100;
		public static readonly Guid CommandSet = new Guid("0e894a9e-1804-46d1-a396-26e0e0a6f251");

		private AttachDebuggerCommand(OleMenuCommandService commandService)
		{
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
			Instance = new AttachDebuggerCommand(commandService);
		}

		private void Execute(object sender, EventArgs e)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			EagleDebugger debugger = EagleDebugger.Instance;
			if (debugger == null)
				return;

			EagleProject project = debugger.GetSolutionEagleProject();
			if (project == null)
			{
				debugger.ShowError("No Eagle project",
					"Couldn't find the Eagle project of this solution: there's no .egproj file in the startup project's or the solution's folder, " +
					"or in their parent folders.");
				return;
			}

			debugger.Attach(project);
		}
	}
}
