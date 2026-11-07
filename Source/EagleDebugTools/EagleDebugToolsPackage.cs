using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace EagleDebugTools
{
	/// <summary>
	/// Visual Studio package for Eagle Debug Tools: F5 (Attach to Eagle), Debug > Attach to Eagle Game... and the options page.
	/// Loads in the background as soon as a solution with a C# project is open, so that F5 is redirected
	/// and the start button reads "Attach to Eagle" without having to use a menu command first.
	/// </summary>
	[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
	[Guid(PackageGuidString)]
	[ProvideMenuResource("Menus.ctmenu", 1)]
	[ProvideOptionPage(typeof(GeneralOptions), "Eagle Debug Tools", "General", 0, 0, true)]
	[ProvideUIContextRule(UIContextHasCSharpProjectGuid, name: "Has CSharp Project",
		expression: "HasCSProject",
		termNames: new[] { "HasCSProject" },
		termValues: new[] { "SolutionHasProjectCapability:CSharp" })]
	[ProvideAutoLoad(UIContextHasCSharpProjectGuid, PackageAutoLoadFlags.BackgroundLoad)]
	public sealed class EagleDebugToolsPackage : AsyncPackage
	{
		/// <summary>Must match guidEagleDebugToolsPackage in EagleDebugToolsPackage.vsct.</summary>
		public const string PackageGuidString = "453fd8e9-75c4-4b4f-a0c5-00003775f117";

		/// <summary>Must match guidUIContextHasCSharpProject in EagleDebugToolsPackage.vsct.</summary>
		private const string UIContextHasCSharpProjectGuid = "66529019-a7f6-4cf0-b694-55066c449bae";

		private StartDebuggingInterceptor m_StartDebuggingInterceptor;

		public static EagleDebugToolsPackage Instance { get; private set; }

		internal GeneralOptions GeneralOptions => (GeneralOptions)GetDialogPage(typeof(GeneralOptions));

		public EagleDebugToolsPackage()
		{
			Instance = this;
		}

		protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
		{
			await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
			await EagleDebugger.InitializeAsync(this);
			await AttachToGameCommand.InitializeAsync(this);
			m_StartDebuggingInterceptor = await StartDebuggingInterceptor.RegisterAsync(this);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && m_StartDebuggingInterceptor != null && ThreadHelper.CheckAccess())
			{
				m_StartDebuggingInterceptor.Unregister();
				m_StartDebuggingInterceptor = null;
			}
			base.Dispose(disposing);
		}
	}
}
