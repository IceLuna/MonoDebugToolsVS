using System;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace EagleDebugTools
{
	/// <summary>
	/// Makes Debug > Start Debugging (F5) and the toolbar's start button attach to the Eagle Editor
	/// when the startup project is an Eagle scripts project and Visual Studio isn't debugging yet.
	/// Both then read "Attach to Eagle". In any other case the commands are left to Visual Studio
	/// (for example F5 still means Continue while debugging, and other projects start as usual).
	///
	/// The toolbar's start button isn't Debug.Start itself: it's the "debug target" menu controller, which shows
	/// the debug target command latched on it (VSConstants.DebugTargetHandler; "Start" for C# class libraries).
	///
	/// Registered as a priority command target, so it sees commands before Visual Studio's own handlers.
	/// </summary>
	internal sealed class StartDebuggingInterceptor : IOleCommandTarget
	{
		private const string ButtonText = "Attach to Eagle";

		// Returned for commands that aren't handled here, so that they go to the next command target
		private const int OLECMDERR_E_NOTSUPPORTED = unchecked((int)0x80040100);
		private const uint OLECMDTEXTF_NAME = 1;

		private static readonly Guid s_StandardCommandSet97 = VSConstants.GUID_VSStandardCommandSet97;
		private const uint StartCommandId = (uint)VSConstants.VSStd97CmdID.Start;

		// Commands the toolbar's start button (debug target menu controller) shows and executes
		private static readonly Guid s_DebugTargetCommandSet = VSConstants.DebugTargetHandler.guidDebugTargetHandlerCmdSet;
		private static readonly uint[] s_DebugTargetCommandIds =
		{
			(uint)VSConstants.DebugTargetHandler.cmdidGenericDebugTarget,
			(uint)VSConstants.DebugTargetHandler.cmdidDebugTargetAnchorItem,
			(uint)VSConstants.DebugTargetHandler.cmdidDebugTargetAnchorItemNoAttachToProcess,
		};

		private IVsRegisterPriorityCommandTarget m_Registry;
		private uint m_Cookie;

		public static async System.Threading.Tasks.Task<StartDebuggingInterceptor> RegisterAsync(AsyncPackage package)
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

			var interceptor = new StartDebuggingInterceptor();
			interceptor.m_Registry = (IVsRegisterPriorityCommandTarget)await package.GetServiceAsync(typeof(SVsRegisterPriorityCommandTarget));
			if (interceptor.m_Registry != null)
				ErrorHandler.ThrowOnFailure(interceptor.m_Registry.RegisterPriorityCommandTarget(0, interceptor, out interceptor.m_Cookie));
			return interceptor;
		}

		public void Unregister()
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			if (m_Registry != null && m_Cookie != 0)
			{
				m_Registry.UnregisterPriorityCommandTarget(m_Cookie);
				m_Cookie = 0;
			}
		}

		public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
		{
			if (cCmds != 1 || !IsStartCommand(pguidCmdGroup, prgCmds[0].cmdID))
				return OLECMDERR_E_NOTSUPPORTED;

			ThreadHelper.ThrowIfNotOnUIThread();
			if (!ShouldHandle(out _, out _))
				return OLECMDERR_E_NOTSUPPORTED;

			prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
			if (pCmdText != IntPtr.Zero)
				SetCommandText(pCmdText, ButtonText);
			return VSConstants.S_OK;
		}

		public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
		{
			if (!IsStartCommand(pguidCmdGroup, nCmdID))
				return OLECMDERR_E_NOTSUPPORTED;

			ThreadHelper.ThrowIfNotOnUIThread();
			if (!ShouldHandle(out EagleProject project, out Project startupProject))
				return OLECMDERR_E_NOTSUPPORTED;

			EagleDebugger.Instance.BuildAndAttach(project, startupProject);
			return VSConstants.S_OK;
		}

		private static bool IsStartCommand(Guid group, uint id)
		{
			if (group == s_StandardCommandSet97)
				return id == StartCommandId;

			return group == s_DebugTargetCommandSet && Array.IndexOf(s_DebugTargetCommandIds, id) >= 0;
		}

		private static bool ShouldHandle(out EagleProject project, out Project startupProject)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			project = null;
			startupProject = null;

			EagleDebugger debugger = EagleDebugger.Instance;
			if (debugger == null || !debugger.IsDesignMode())
				return false;

			project = debugger.GetStartupEagleProject(out startupProject);
			return project != null;
		}

		// Fills an OLECMDTEXT: { uint cmdtextf; uint cwActual; uint cwBuf; wchar rgwz[cwBuf]; }
		private static void SetCommandText(IntPtr pCmdText, string text)
		{
			uint flags = (uint)Marshal.ReadInt32(pCmdText, 0);
			if ((flags & OLECMDTEXTF_NAME) == 0)
				return;

			int bufferLength = Marshal.ReadInt32(pCmdText, 8);
			if (bufferLength <= 0)
				return;

			int count = Math.Min(text.Length, bufferLength - 1);
			Marshal.Copy(text.ToCharArray(0, count), 0, IntPtr.Add(pCmdText, 12), count);
			Marshal.WriteInt16(pCmdText, 12 + count * 2, 0);
			Marshal.WriteInt32(pCmdText, 4, count + 1);
		}
	}
}
