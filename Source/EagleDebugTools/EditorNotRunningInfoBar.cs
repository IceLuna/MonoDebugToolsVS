using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace EagleDebugTools
{
	/// <summary>
	/// Info bar shown at the top of Visual Studio when attaching fails because no Eagle Editor has the project open.
	/// Its "Run Eagle Editor" button starts the editor with the project. If a game of the project is running,
	/// "Attach to Running Game" attaches to it instead.
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

			string message = "Eagle Editor isn't running with this project (" + project.Name + ").";
			if (m_Factory == null || m_Host == null)
			{
				// No info bar support: fall back to a message box
				int answer = VsShellUtilities.ShowMessageBox(m_Package, message + " Start it?", "Eagle Debug Tools",
					OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
				if (answer == (int)Microsoft.VisualStudio.VSConstants.MessageBoxResult.IDYES)
					RunEditor(project);
				return;
			}

			var actions = new System.Collections.Generic.List<InfoBarButton> { new InfoBarButton("Run Eagle Editor", RunEditorAction) };
			if (EagleDebugger.IsGameRunning(project))
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

		private void RunEditor(EagleProject project)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			string error = EagleEditorLauncher.Launch(project);
			if (error != null)
			{
				VsShellUtilities.ShowMessageBox(m_Package, error, "Couldn't start the Eagle Editor",
					OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
				return;
			}

			EagleDebugger.Instance?.SetStatus("Starting the Eagle Editor with " + project.Name + ". Press F5 to attach once the project is open.");
		}
	}
}
