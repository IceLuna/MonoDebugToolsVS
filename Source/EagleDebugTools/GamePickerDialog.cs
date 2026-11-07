using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace EagleDebugTools
{
	/// <summary>
	/// Lets the user pick one of the running debuggable Eagle games. Games of the solution's project are listed first.
	/// </summary>
	internal sealed class GamePickerDialog : DialogWindow
	{
		private readonly ListBox m_List;

		private GamePickerDialog(IEnumerable<DebuggerInfoFile> games, EagleProject project)
		{
			Title = "Attach to Eagle Game";
			Width = 620;
			Height = 340;
			MinWidth = 400;
			MinHeight = 220;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;
			ResizeMode = ResizeMode.CanResizeWithGrip;
			ShowInTaskbar = false;
			HasMinimizeButton = false;
			HasMaximizeButton = false;

			var root = new DockPanel { Margin = new Thickness(12) };

			var header = new TextBlock
			{
				Text = "Select the running game to attach to:",
				Margin = new Thickness(0, 0, 0, 8),
			};
			DockPanel.SetDock(header, Dock.Top);
			root.Children.Add(header);

			var buttons = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Margin = new Thickness(0, 10, 0, 0),
			};
			var attachButton = new Button { Content = "Attach", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
			var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
			attachButton.Click += (sender, e) => Accept();
			buttons.Children.Add(attachButton);
			buttons.Children.Add(cancelButton);
			DockPanel.SetDock(buttons, Dock.Bottom);
			root.Children.Add(buttons);

			m_List = new ListBox();
			m_List.MouseDoubleClick += (sender, e) => Accept();
			IEnumerable<DebuggerInfoFile> ordered = project == null ? games : games.OrderBy(game => project.IsProjectOf(game) ? 0 : 1);
			foreach (DebuggerInfoFile game in ordered)
				m_List.Items.Add(new ListBoxItem { Content = Describe(game), Tag = game, Padding = new Thickness(4, 3, 4, 3) });
			m_List.SelectedIndex = 0;
			root.Children.Add(m_List);

			Content = root;
		}

		/// <summary>Returns the picked game, or null if the dialog was cancelled</summary>
		public static DebuggerInfoFile Pick(IEnumerable<DebuggerInfoFile> games, EagleProject project)
		{
			var dialog = new GamePickerDialog(games, project);
			if (dialog.ShowModal() != true)
				return null;

			return (dialog.m_List.SelectedItem as ListBoxItem)?.Tag as DebuggerInfoFile;
		}

		private void Accept()
		{
			if (m_List.SelectedItem != null)
				DialogResult = true;
		}

		private static string Describe(DebuggerInfoFile game)
		{
			string started = string.Empty;
			try
			{
				using (Process process = Process.GetProcessById(game.ProcessId))
					started = "   started " + process.StartTime.ToString("T");
			}
			catch (Exception)
			{
				// Exited in the meantime, or no access
			}

			string project = string.IsNullOrEmpty(game.Project) ? "Unknown project" : game.Project;
			return project + "   (process " + game.ProcessId + started + ")" + Environment.NewLine + game.Executable;
		}
	}
}
