using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DHT.Desktop.Common;
using DHT.Desktop.Dialogs.File;
using DHT.Desktop.Dialogs.Message;
using DHT.Desktop.Dialogs.Progress;
using DHT.Desktop.Dialogs.TextBox;
using DHT.Desktop.Main.Pages;
using DHT.Server.Data;
using DHT.Server.Data.Settings;
using DHT.Server.Database;
using DHT.Server.Database.Import;
using DHT.Server.Database.Sqlite.Schema;
using DHT.Utils.Logging;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.VisualBasic;
using Microsoft.Win32;

namespace DHT.Desktop.Main.Screens;

sealed partial class WelcomeScreenModel : ObservableObject {
	public string Version => Program.Version;
	
	[ObservableProperty(Setter = Access.Private)]
	private bool isOpenOrCreateDatabaseButtonEnabled = true;
	
	public event EventHandler<IDatabaseFile>? DatabaseSelected; 
    private readonly Log log;

	private readonly Window window;
    private IDatabaseFile? db;

	private string? dbFilePath;

	[Obsolete("Designer")]
	public WelcomeScreenModel() : this(null!) {}

	public WelcomeScreenModel(Window window) {
		this.window = window;
	}
    
    public async Task ImportLegacyArchive() {
        var paths = await window.StorageProvider.OpenFiles(new FilePickerOpenOptions {
            Title = "Open Legacy DHT Archive",
            AllowMultiple = true
        });

        await OpenOrCreateDatabase();
        if (paths.Length > 0) {
            await ProgressDialog.Show(window, "Legacy Archive Import", async (dialog, callback) => await ImportLegacyArchiveFromPaths(db, paths, dialog, callback));
        }
    }

    private static async Task ImportLegacyArchiveFromPaths(IDatabaseFile target, string[] paths, ProgressDialog dialog, IProgressCallback callback) {
        var fakeSnowflake = new FakeSnowflake();

        await PerformImport(target, paths, dialog, callback, "Legacy Archive Import", "Legacy Archive Error", "archive file", async path => {
            await using var jsonStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            return await LegacyArchiveImport.Read(jsonStream, target, fakeSnowflake, async servers => {
                SynchronizationContext? prevSyncContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
                Dictionary<DHT.Server.Data.Server, ulong>? result = await Dispatcher.UIThread.InvokeAsync(() => AskForServerIds(dialog, servers));
                SynchronizationContext.SetSynchronizationContext(prevSyncContext);
                return result;
            });
        });
    }
    
    private static async Task<Dictionary<DHT.Server.Data.Server, ulong>?> AskForServerIds(Window window, DHT.Server.Data.Server[] servers) {
		static bool IsValidSnowflake(string value) {
			return string.IsNullOrEmpty(value) || ulong.TryParse(value, out _);
		}

		var items = new List<TextBoxItem<DHT.Server.Data.Server>>();

		foreach (var server in servers.OrderBy(static server => server.Type).ThenBy(static server => server.Name)) {
			items.Add(new TextBoxItem<DHT.Server.Data.Server>(server) {
				Title = server.Name + " (" + ServerTypes.ToNiceString(server.Type) + ")",
				ValidityCheck = IsValidSnowflake
			});
		}

		var model = new TextBoxDialogModel<DHT.Server.Data.Server>(items) {
			Title = "Imported Server IDs",
			Description = "Please fill in the IDs of servers and direct messages. First enable Developer Mode in Discord, then right-click each server or direct message, click 'Copy ID', and paste it into the input field. If a server no longer exists, leave its input field empty to use a random ID."
		};

		// var dialog = new TextBoxDialog { DataContext = model };
		// var result = await dialog.ShowDialog<DialogResult.OkCancel>(window);
        
        return model.ValidItems
            .Where(static item => !string.IsNullOrEmpty(item.Value))
            .ToDictionary(static item => item.Item, static item => ulong.Parse(item.Value));
    }

	private static async Task PerformImport(IDatabaseFile target, string[] paths, ProgressDialog dialog, IProgressCallback callback, string neutralDialogTitle, string errorDialogTitle, string itemName, Func<string, Task<bool>> performImport) {
		int total = paths.Length;

		int successful = 0;
		int finished = 0;

		foreach (string path in paths) {
			await callback.Update(Path.GetFileName(path), finished, total);
			++finished;

			if (!File.Exists(path)) {
				await Dialog.ShowOk(dialog, errorDialogTitle, "File '" + Path.GetFileName(path) + "' no longer exists.");
				continue;
			}

			try {
				if (await performImport(path)) {
					++successful;
				}
			} catch (Exception ex) {
				// Log.Error(ex);
				await Dialog.ShowOk(dialog, errorDialogTitle, "File '" + Path.GetFileName(path) + "' could not be imported: " + ex.Message);
			}
		}

		await callback.Update("Done", finished, total);
	}


	public async Task OpenOrCreateDatabase()
    {
		IsOpenOrCreateDatabaseButtonEnabled = false;
        try
        {
            var path = await DatabaseGui.NewOpenOrCreateDatabaseFileDialog(window, Path.GetDirectoryName(dbFilePath));
            if (path != null)
            {
                await OpenOrCreateDatabaseFromPath(path, true);
            }
        }
        catch(Exception e)
        {
            throw e;
        } 
        finally {
			IsOpenOrCreateDatabaseButtonEnabled = true;
		}
	}

    public async void BindShellExtension()
    {
        RegistryKey SoftwareClasses = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Classes", true);
        RegistryKey? DHTClass = SoftwareClasses.OpenSubKey(".dht", true);
        if (DHTClass == null)
        {
            DHTClass = SoftwareClasses.CreateSubKey(".dht");
        }
        DHTClass.SetValue("", "DHT.Internal");
        
        
        RegistryKey? DHTInternalClass = SoftwareClasses.OpenSubKey("DHT.Internal", true);
        RegistryKey? CommandClass = null;
        string path = Assembly.GetExecutingAssembly().Location;
        path = Strings.Replace(path, ".dll", ".exe");
        
        if (DHTInternalClass == null)
        {
            DHTInternalClass = SoftwareClasses.CreateSubKey("DHT.Internal");
            RegistryKey ShellClass = DHTInternalClass.CreateSubKey("shell");
            RegistryKey DefaultIcon = DHTInternalClass.CreateSubKey("DefaultIcon");
            DefaultIcon.SetValue("", path);
            RegistryKey OpenClass = ShellClass.CreateSubKey("Open");
            CommandClass = OpenClass.CreateSubKey("command");

        }
        DHTInternalClass.SetValue("", "DHT Internal Database");
        if (CommandClass == null)
        {
            CommandClass = DHTInternalClass.OpenSubKey(@"shell\Open\command", true);
        }

        
        CommandClass.SetValue("", path + " -db \"%1\"");
    }
    
	public async Task OpenOrCreateDatabaseFromPath(string path, bool handleInvocation = true)
    {
		dbFilePath = path;
		bool isNew = !File.Exists(path);
		
        db = await DatabaseGui.TryOpenOrCreateDatabaseFromPath(path, window, new SchemaUpgradeCallbacks(window));
		if (db == null) {
			return;
		}
		
		if (isNew && await Dialog.ShowYesNo(window, "Automatic Downloads", "Do you want to automatically download files hosted on Discord? You can change this later in the Downloads tab.") == DialogResult.YesNo.Yes) {
			await db.Settings.Set(SettingsKey.DownloadsAutoStart, true);
		}

        if (handleInvocation)
        {
            DatabaseSelected?.Invoke(this, db);
        } 
    }

	private sealed class SchemaUpgradeCallbacks : ISchemaUpgradeCallbacks {
		private readonly Window window;
		
		public SchemaUpgradeCallbacks(Window window) {
			this.window = window;
		}

		public async Task<bool> CanUpgrade() {
			return DialogResult.YesNo.Yes == await DatabaseGui.ShowCanUpgradeDatabaseDialog(window);
		}

		public async Task Start(int versionSteps, Func<ISchemaUpgradeCallbacks.IProgressReporter, Task> doUpgrade) {
			async Task StartUpgrade(IReadOnlyList<IProgressCallback> callbacks) {
				var reporter = new ProgressReporter(versionSteps, callbacks);
				await reporter.NextVersion();
				await Task.Delay(TimeSpan.FromMilliseconds(800));
				await doUpgrade(reporter);
				await Task.Delay(TimeSpan.FromMilliseconds(600));
			}
			
			await new ProgressDialog { DataContext = new ProgressDialogModel("Upgrading Database", StartUpgrade, progressItems: 3) }.ShowProgressDialog(window);
		}

		private sealed class ProgressReporter : ISchemaUpgradeCallbacks.IProgressReporter {
			private readonly IReadOnlyList<IProgressCallback> callbacks;
			
			private readonly int versionSteps;
			private int versionProgress = 0;
			
			public ProgressReporter(int versionSteps, IReadOnlyList<IProgressCallback> callbacks) {
				this.callbacks = callbacks;
				this.versionSteps = versionSteps;
			}

			public async Task NextVersion() {
				await callbacks[0].Update("Upgrading schema version...", versionProgress++, versionSteps);
				await HideChildren(0);
			}

			public async Task MainWork(string message, int finishedItems, int totalItems) {
				await callbacks[1].Update(message, finishedItems, totalItems);
				await HideChildren(1);
			}

			public async Task SubWork(string message, int finishedItems, int totalItems) {
				await callbacks[2].Update(message, finishedItems, totalItems);
				await HideChildren(2);
			}

			private async Task HideChildren(int parentIndex) {
				for (int i = parentIndex + 1; i < callbacks.Count; i++) {
					await callbacks[i].Hide();
				}
			}
		}
	}

	public async Task ShowAboutDialog() {
		await new AboutWindow { DataContext = new AboutWindowModel() }.ShowDialog(window);
	}

	public void Exit() {
		window.Close();
	}
}
