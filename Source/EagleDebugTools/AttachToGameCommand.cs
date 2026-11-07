using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace EagleDebugTools
{
	/// <summary>
	/// "Debug > Attach to Eagle Game..." command: attaches to a running Eagle game (see EagleDebugger.AttachToGame).
	/// </summary>
	internal sealed class AttachToGameCommand
	{
		/// <summary>Must match AttachToEagleGameCommandId / guidEagleDebugToolsCmdSet in EagleDebugToolsPackage.vsct.</summary>
		public const int CommandId = 0x0100;
		public static readonly Guid CommandSet = new Guid("0b05df85-5954-4c00-815a-910150560437");

		public static async Task InitializeAsync(AsyncPackage package)
		{
			// AddCommand requires the UI thread
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

			if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
				commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
		}

		private static void Execute(object sender, EventArgs e)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			EagleDebugger.Instance?.AttachToGame();
		}
	}
}
