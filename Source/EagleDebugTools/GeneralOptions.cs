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
		[DisplayName("Connection Port")]
		[Description("Port of the engine's Mono debugger agent. Must match the address in ScriptEngine::Init() (default: 2550).")]
		public int ConnectionPort { get; set; } = 2550;

		[Category("Debugging")]
		[DisplayName("Allow Property Evaluation")]
		[Description("Lets the debugger run script code (property getters, ToString(), method calls in Watch) to display values. " +
			"This code runs on the paused engine thread; if it calls into the engine and blocks, both Visual Studio and the engine can freeze. " +
			"When disabled, fields are still shown. Takes effect on the next attach. Default: True")]
		public bool AllowPropertyEvaluation { get; set; } = true;
	}
}
