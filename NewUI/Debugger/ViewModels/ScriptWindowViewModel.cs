﻿using Avalonia.Controls;
using Mesen.Config;
using Mesen.Debugger.Utilities;
using Mesen.Debugger.Windows;
using Mesen.Interop;
using Mesen.Utilities;
using Mesen.ViewModels;
using Mesen.Windows;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Mesen.Localization;

namespace Mesen.Debugger.ViewModels
{
	public class ScriptWindowViewModel : ViewModelBase
	{
		public ScriptWindowConfig Config { get; } = ConfigManager.Config.Debug.ScriptWindow;
		public FontConfig Font { get; } = ConfigManager.Config.Debug.Font;

		[Reactive] public string Code { get; set; } = "";
		[Reactive] public string FilePath { get; set; } = "";
		[Reactive] public int ScriptId { get; set; } = -1;
		[Reactive] public string Log { get; set; } = "";
		[Reactive] public string ScriptName { get; set; } = "";

		[ObservableAsProperty] public string WindowTitle { get; } = "";

		private string _originalText = "";
		private ScriptWindow? _wnd = null;
		private FileSystemWatcher _fileWatcher = new();

		private ContextMenuAction _recentScriptsAction = new();

		[Reactive] public List<ContextMenuAction> FileMenuActions { get; private set; } = new();
		[Reactive] public List<ContextMenuAction> ScriptMenuActions { get; private set; } = new();
		[Reactive] public List<ContextMenuAction> HelpMenuActions { get; private set; } = new();
		[Reactive] public List<ContextMenuAction> ToolbarActions { get; private set; } = new();

		public ScriptWindowViewModel()
		{
			this.WhenAnyValue(x => x.ScriptName).Select(x => {
				string wndTitle = ResourceHelper.GetViewLabel(nameof(ScriptWindow), "wndTitle");
				if(!string.IsNullOrWhiteSpace(x)) {
					return wndTitle + " - " + x;
				}
				return wndTitle;
			}).ToPropertyEx(this, x => x.WindowTitle);
		}

		public void InitActions(ScriptWindow wnd)
		{
			if(wnd == null) {
				throw new Exception("Invalid parent window");
			}
			
			_wnd = wnd;

			_recentScriptsAction = new ContextMenuAction() {
				ActionType = ActionType.RecentScripts,
				SubActions = new()
			};
			_recentScriptsAction.IsEnabled = () => _recentScriptsAction.SubActions.Count > 0;

			ScriptMenuActions = GetScriptMenuAction();
			ToolbarActions = GetScriptMenuAction();

			FileMenuActions = new() {
				new ContextMenuAction() {
					ActionType = ActionType.NewScript,
					Shortcut = () => ConfigManager.Config.Debug.Shortcuts.Get(DebuggerShortcut.ScriptWindow_NewScript),
					OnClick = () => {
						new ScriptWindow(new ScriptWindowViewModel()).Show();
					}
				},
				new ContextMenuAction() {
					ActionType = ActionType.Open,
					Shortcut = () => ConfigManager.Config.Debug.Shortcuts.Get(DebuggerShortcut.ScriptWindow_OpenScript),
					OnClick = () => OpenScript()
				},
				new ContextMenuAction() {
					ActionType = ActionType.Save,
					Shortcut = () => ConfigManager.Config.Debug.Shortcuts.Get(DebuggerShortcut.ScriptWindow_SaveScript),
					OnClick = async () => await SaveScript()
				},
				new ContextMenuAction() {
					ActionType = ActionType.SaveAs,
					OnClick = async () => await SaveAs(Path.GetFileName(FilePath))
				},
				new ContextMenuSeparator(),
				new ContextMenuAction() {
					ActionType = ActionType.BuiltInScripts,
					SubActions = GetBuiltInScriptActions()
				},
				_recentScriptsAction,
				new ContextMenuSeparator(),
				new ContextMenuAction() {
					ActionType = ActionType.Exit,
					OnClick = () => _wnd?.Close()
				}
			};

			HelpMenuActions = new() {
				new ContextMenuAction() {
					ActionType = ActionType.HelpApiReference,
					OnClick = () => Process.Start(new ProcessStartInfo() { FileName = "https://www.mesen.ca/snes/ApiReference.php", UseShellExecute = true })
				}
			};

			UpdateRecentScriptsMenu();
			DebugShortcutManager.RegisterActions(_wnd, ScriptMenuActions);
			DebugShortcutManager.RegisterActions(_wnd, FileMenuActions);
		}

		private List<object> GetBuiltInScriptActions()
		{
			List<object> actions = new();
			Assembly assembly = Assembly.GetExecutingAssembly();
			foreach(string name in assembly.GetManifestResourceNames()) {
				if(Path.GetExtension(name).ToLower() == ".lua") {
					string scriptName = name.Substring(name.LastIndexOf('.', name.Length - 5) + 1);

					actions.Add(new ContextMenuAction() {
						ActionType = ActionType.Custom,
						CustomText = scriptName,
						OnClick = () => {
							using Stream? stream = assembly.GetManifestResourceStream(name);
							if(stream != null) {
								using StreamReader sr = new StreamReader(stream);
								LoadScriptFromString(sr.ReadToEnd());
								ScriptName = scriptName;
							}
						}
					});
				}
			}
			actions.Sort((a, b) => ((ContextMenuAction)a).Name.CompareTo(((ContextMenuAction)b).Name));
			return actions;
		}

		public async void RunScript()
		{
			if(Config.SaveScriptBeforeRun && !string.IsNullOrWhiteSpace(FilePath)) {
				await SaveScript();
			}

			ScriptId = DebugApi.LoadScript(ScriptName, Code, ScriptId);
		}

		private List<ContextMenuAction> GetScriptMenuAction()
		{
			 return new() {
				new ContextMenuAction() {
					ActionType = ActionType.RunScript,
					Shortcut = () => ConfigManager.Config.Debug.Shortcuts.Get(DebuggerShortcut.ScriptWindow_RunScript),
					IsEnabled = () => ScriptId < 0,
					OnClick = RunScript
				},
				new ContextMenuAction() {
					ActionType = ActionType.StopScript,
					Shortcut = () => ConfigManager.Config.Debug.Shortcuts.Get(DebuggerShortcut.ScriptWindow_StopScript),
					IsEnabled = () => ScriptId >= 0,
					OnClick = StopScript
				},
				new ContextMenuSeparator(),
				new ContextMenuAction() {
					ActionType = ActionType.OpenDebugSettings,
					OnClick = () => DebuggerConfigWindow.Open(DebugConfigWindowTab.ScriptWindow, _wnd)
				}
			};
		}

		public void StopScript()
		{
			DebugApi.RemoveScript(ScriptId);
			ScriptId = -1;
		}

		private string? InitialFolder
		{
			get
			{
				if(ConfigManager.Config.Debug.ScriptWindow.RecentScripts.Count > 0) {
					return Path.GetDirectoryName(ConfigManager.Config.Debug.ScriptWindow.RecentScripts[0]);
				}
				return null;
			}
		}

		private void UpdateRecentScriptsMenu()
		{
			_recentScriptsAction.SubActions = ConfigManager.Config.Debug.ScriptWindow.RecentScripts.Select(x => new ContextMenuAction() {
				ActionType = ActionType.Custom,
				CustomText = x,
				OnClick = () => LoadScript(x)
			}).ToList<object>();
		}

		private async void OpenScript()
		{
			if(!await SavePrompt()) {
				return;
			}

			string? filename = await FileDialogHelper.OpenFile(InitialFolder, _wnd, FileDialogHelper.LuaExt);
			if(filename != null) {
				LoadScript(filename);
			}
		}

		private void AddRecentScript(string filename)
		{
			ConfigManager.Config.Debug.ScriptWindow.AddRecentScript(filename);
			UpdateRecentScriptsMenu();
		}

		private void LoadScriptFromString(string scriptContent)
		{
			Code = scriptContent;
			_originalText = Code;

			if(Config.AutoStartScriptOnLoad) {
				RunScript();
			}
		}

		private void LoadScript(string filename)
		{
			if(File.Exists(filename)) {
				string? code = FileHelper.ReadAllText(filename);
				if(code != null) {
					AddRecentScript(filename);
					SetFilePath(filename);
					LoadScriptFromString(code);
				}
			}
		}

		private void SetFilePath(string filename)
		{
			FilePath = filename;
			ScriptName = Path.GetFileName(filename);

			_fileWatcher.EnableRaisingEvents = false;

			_fileWatcher = new(Path.GetDirectoryName(FilePath) ?? "", Path.GetFileName(FilePath));
			_fileWatcher.Changed += (s, e) => {
				if(Config.AutoReloadScriptWhenFileChanges) {
					System.Threading.Thread.Sleep(100);
					LoadScript(FilePath);
				}
			};
			_fileWatcher.EnableRaisingEvents = true;
		}

		private async Task<bool> SavePrompt()
		{
			if(_originalText != Code) {
				DialogResult result = await MesenMsgBox.Show(_wnd, "You have unsaved changes for this script - would you like to save them?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
				if(result == DialogResult.Yes) {
					return !(await SaveScript());
				} else if(result == DialogResult.Cancel) {
					return false;
				}
			}
			return true;
		}

		private async Task<bool> SaveScript()
		{
			if(!string.IsNullOrWhiteSpace(FilePath)) {
				if(_originalText != Code) {
					if(FileHelper.WriteAllText(FilePath, Code, Encoding.UTF8)) {
						_originalText = Code;
					}
				}
				return true;
			} else {
				return await SaveAs("NewScript.lua");
			}
		}

		private async Task<bool> SaveAs(string newName)
		{
			string? filename = await FileDialogHelper.SaveFile(InitialFolder, newName, _wnd, FileDialogHelper.LuaExt);
			if(filename != null) {
				if(FileHelper.WriteAllText(filename, Code, Encoding.UTF8)) {
					AddRecentScript(filename);
					SetFilePath(filename);
					return true;
				}
			}
			return false;
		}
	}
}
