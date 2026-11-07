using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace EagleDebugTools
{
	/// <summary>
	/// Tools > Options > Eagle Debug Tools > General
	/// </summary>
	public class GeneralOptions : DialogPage
	{
		[Category("Debugging")]
		[DisplayName("Build Before Attaching")]
		[Description("F5 (Attach to Eagle) builds the scripts project first, like Start Debugging does for other projects. " +
			"The Eagle Editor reloads the rebuilt scripts by itself (while it isn't playing). Default: True")]
		public bool BuildBeforeAttaching { get; set; } = true;

		[Category("Debugging")]
		[DisplayName("Allow Property Evaluation")]
		[Description("Lets the debugger run script code (property getters, ToString(), method calls in Watch) to display values. " +
			"This code runs on the paused engine thread; if it calls into the engine and blocks, both Visual Studio and the engine can freeze. " +
			"When disabled, fields are still shown. Takes effect on the next attach. Default: True")]
		public bool AllowPropertyEvaluation { get; set; } = true;
	}
}
