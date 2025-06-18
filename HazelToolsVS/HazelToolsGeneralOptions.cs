using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace HazelToolsVS
{
	public class HazelToolsGeneralOptions : DialogPage
	{

		[Category("Debugging")]
		[DisplayName("Connection Port")]
		[Description("The port that Engine is expected to use for the debugger. This value should match the Engine Debugger Port value")]
		public int ConnectionPort { get; set; } = 2550;

		[Category("Debugging")]
		[DisplayName("Maximum Connection Attempts")]
		[Description("Determines how many connection attempts it can make if it fails to attach to the Engine (0 means inifite attempts. Default: 1)")]
		public int MaxConnectionAttempts { get; set; } = 1;

	}
}
