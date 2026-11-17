using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace EagleDebugTools
{
	/// <summary>
	/// Info bar shown at the top of Visual Studio when F5 is pressed while no Eagle Editor has the project open
	/// but a game of the project is running, so F5 could mean either:
	/// "Start Eagle and Attach" starts the editor with the project and attaches to it (see EagleDebugger.StartEditorAndAttach),
	/// "Attach to Running Game" attaches to the game instead.
	/// </summary>
	internal sealed class EditorNotRunningInfoBar : IVsInfoBarUIEvents
	{
		private const string RunEditorAction = "RunEditor";
		private const string AttachToGameAction = "AttachToGame";

		private readonly AsyncPackage m_Package;
		private readonly IVsInfoBarUIFactory m_Factory;
		private readonly IVsInfoBarHost m_Host;

		private IVsInfoBarUIElement m_Element;
		private uint m_EventsCookie;
		private EagleProject m_Project;

		private EditorNotRunningInfoBar(AsyncPackage package, IVsInfoBarUIFactory factory, IVsInfoBarHost host)
		{
			m_Package = package;
			m_Factory = factory;
			m_Host = host;
		}

		public static async System.Threading.Tasks.Task<EditorNotRunningInfoBar> CreateAsync(AsyncPackage package)
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

			var factory = (IVsInfoBarUIFactory)await package.GetServiceAsync(typeof(SVsInfoBarUIFactory));
			var shell = (IVsShell)await package.GetServiceAsync(typeof(SVsShell));

			IVsInfoBarHost host = null;
			if (shell != null && shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObject) == Microsoft.VisualStudio.VSConstants.S_OK)
				host = hostObject as IVsInfoBarHost;

			return new EditorNotRunningInfoBar(package, factory, host);
		}

		public void Show(EagleProject project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			Close();
			m_Project = project;

			bool bGameRunning = EagleDebugger.IsGameRunning(project);
			string message = "No Eagle Editor has this project (" + project.Name + ") open" + (bGameRunning ? ", but a game of it is running." : ".");
			if (m_Factory == null || m_Host == null)
			{
				// No info bar support: fall back to a message box
				if (!bGameRunning)
				{
					int answer = VsShellUtilities.ShowMessageBox(m_Package, message + " Start it and attach?", "Eagle Debug Tools",
						OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
					if (answer == (int)Microsoft.VisualStudio.VSConstants.MessageBoxResult.IDYES)
						RunEditor(project);
				}
				else
				{
					int answer = VsShellUtilities.ShowMessageBox(m_Package,
						message + System.Environment.NewLine + System.Environment.NewLine + "Yes: start the Eagle Editor and attach to it" + System.Environment.NewLine + "No: attach to the running game",
						"Eagle Debug Tools", OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNOCANCEL, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
					if (answer == (int)Microsoft.VisualStudio.VSConstants.MessageBoxResult.IDYES)
						RunEditor(project);
					else if (answer == (int)Microsoft.VisualStudio.VSConstants.MessageBoxResult.IDNO)
						EagleDebugger.Instance?.AttachToGame();
				}
				return;
			}

			var actions = new System.Collections.Generic.List<InfoBarButton> { new InfoBarButton("Start Eagle and Attach", RunEditorAction) };
			if (bGameRunning)
				actions.Add(new InfoBarButton("Attach to Running Game", AttachToGameAction));

			var model = new InfoBarModel(
				new[] { new InfoBarTextSpan(message) },
				actions,
				KnownMonikers.StatusInformation,
				true); // Close button

			m_Element = m_Factory.CreateInfoBar(model);
			m_Element.Advise(this, out m_EventsCookie);
			m_Host.AddInfoBar(m_Element);
		}

		public void Close()
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			IVsInfoBarUIElement element = m_Element;
			if (element == null)
				return;

			element.Unadvise(m_EventsCookie);
			m_Element = null;
			m_Project = null;
			element.Close();
		}

		public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			EagleProject project = m_Project;
			Close();

			if (AttachToGameAction.Equals(actionItem.ActionContext))
				EagleDebugger.Instance?.AttachToGame();
			else if (project != null)
				RunEditor(project);
		}

		// Closed with its close button
		public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			if (ReferenceEquals(infoBarUIElement, m_Element))
			{
				infoBarUIElement.Unadvise(m_EventsCookie);
				m_Element = null;
				m_Project = null;
			}
		}

		// Starts the editor with the project and attaches once it has opened it (errors are shown there)
		private void RunEditor(EagleProject project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			EagleDebugger.Instance?.StartEditorAndAttach(project);
		}
	}
}
