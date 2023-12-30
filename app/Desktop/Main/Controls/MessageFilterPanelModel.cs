using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DHT.Desktop.Common;
using DHT.Desktop.Dialogs.CheckBox;
using DHT.Desktop.Dialogs.Message;
using DHT.Desktop.Dialogs.Progress;
using DHT.Server;
using DHT.Server.Data;
using DHT.Server.Data.Filters;
using DHT.Server.Database;
using DHT.Utils.Tasks;

namespace DHT.Desktop.Main.Controls;

sealed partial class MessageFilterPanelModel : ObservableObject, IDisposable {
	private static readonly HashSet<string> FilterProperties = [
		nameof(FilterByDate),
		nameof(StartDate),
		nameof(EndDate),
		nameof(FilterByChannel),
		nameof(IncludedChannels),
		nameof(FilterByUser),
		nameof(IncludedUsers)
	];

	public string FilterStatisticsText { get; private set; } = "";

	public event PropertyChangedEventHandler? FilterPropertyChanged;

	public bool HasAnyFilters => FilterByDate || FilterByChannel || FilterByUser;

	[ObservableProperty]
	private bool filterByDate = false;

	[ObservableProperty]
	private DateTime? startDate = null;

	[ObservableProperty]
	private DateTime? endDate = null;

	[ObservableProperty]
	private bool filterByChannel = false;

	[ObservableProperty]
	private HashSet<ulong>? includedChannels = null;

	[ObservableProperty]
	private bool filterByUser = false;

	[ObservableProperty]
	private HashSet<ulong>? includedUsers = null;

	[ObservableProperty]
	private string channelFilterLabel = "";

	[ObservableProperty]
	private string userFilterLabel = "";

	private readonly Window window;
	private readonly State state;
	private readonly string verb;

	private readonly RestartableTask<long> exportedMessageCountTask;
	private long? exportedMessageCount;
	private long? totalMessageCount;

	[Obsolete("Designer")]
	public MessageFilterPanelModel() : this(null!, State.Dummy) {}

	public MessageFilterPanelModel(Window window, State state, string verb = "Matches") {
		this.window = window;
		this.state = state;
		this.verb = verb;

		this.exportedMessageCountTask = new RestartableTask<long>(SetExportedMessageCount, TaskScheduler.FromCurrentSynchronizationContext());

		UpdateFilterStatistics();
		UpdateChannelFilterLabel();
		UpdateUserFilterLabel();

		PropertyChanged += OnPropertyChanged;
		state.Db.Statistics.PropertyChanged += OnDbStatisticsChanged;
	}

	public void Dispose() {
		exportedMessageCountTask.Cancel();
		state.Db.Statistics.PropertyChanged -= OnDbStatisticsChanged;
	}

	private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) {
		if (e.PropertyName != null && FilterProperties.Contains(e.PropertyName)) {
			UpdateFilterStatistics();
			FilterPropertyChanged?.Invoke(sender, e);
		}

		if (e.PropertyName is nameof(FilterByChannel) or nameof(IncludedChannels)) {
			UpdateChannelFilterLabel();
		}
		else if (e.PropertyName is nameof(FilterByUser) or nameof(IncludedUsers)) {
			UpdateUserFilterLabel();
		}
	}

	private void OnDbStatisticsChanged(object? sender, PropertyChangedEventArgs e) {
		if (e.PropertyName == nameof(DatabaseStatistics.TotalMessages)) {
			totalMessageCount = state.Db.Statistics.TotalMessages;
			UpdateFilterStatistics();
		}
		else if (e.PropertyName == nameof(DatabaseStatistics.TotalChannels)) {
			UpdateChannelFilterLabel();
		}
		else if (e.PropertyName == nameof(DatabaseStatistics.TotalUsers)) {
			UpdateUserFilterLabel();
		}
	}

	private void UpdateChannelFilterLabel() {
		long total = state.Db.Statistics.TotalChannels;
		long included = FilterByChannel && IncludedChannels != null ? IncludedChannels.Count : total;
		ChannelFilterLabel = "Selected " + included.Format() + " / " + total.Pluralize("channel") + ".";
	}

	private void UpdateUserFilterLabel() {
		long total = state.Db.Statistics.TotalUsers;
		long included = FilterByUser && IncludedUsers != null ? IncludedUsers.Count : total;
		UserFilterLabel = "Selected " + included.Format() + " / " + total.Pluralize("user") + ".";
	}

	private void UpdateFilterStatistics() {
		var filter = CreateFilter();
		if (filter.IsEmpty) {
			exportedMessageCountTask.Cancel();
			exportedMessageCount = totalMessageCount;
			UpdateFilterStatisticsText();
		}
		else {
			exportedMessageCount = null;
			UpdateFilterStatisticsText();
			exportedMessageCountTask.Restart(cancellationToken => state.Db.Messages.Count(filter, cancellationToken));
		}
	}

	private void SetExportedMessageCount(long exportedMessageCount) {
		this.exportedMessageCount = exportedMessageCount;
		UpdateFilterStatisticsText();
	}

	private void UpdateFilterStatisticsText() {
		var exportedMessageCountStr = exportedMessageCount?.Format() ?? "(...)";
		var totalMessageCountStr = totalMessageCount?.Format() ?? "(...)";

		FilterStatisticsText = verb + " " + exportedMessageCountStr + " out of " + totalMessageCountStr + " message" + (totalMessageCount is null or 1 ? "." : "s.");
		OnPropertyChanged(nameof(FilterStatisticsText));
	}

	public async Task OpenChannelFilterDialog() {
		async Task<List<CheckBoxItem<ulong>>> PrepareChannelItems(ProgressDialog dialog) {
			var items = new List<CheckBoxItem<ulong>>();
			var servers = await state.Db.Servers.Get().ToDictionaryAsync(static server => server.Id);

			await foreach (var channel in state.Db.Channels.Get()) {
				var channelId = channel.Id;
				var channelName = channel.Name;

				string title;
				if (servers.TryGetValue(channel.Server, out var server)) {
					var titleBuilder = new StringBuilder();
					var serverType = server.Type;

					titleBuilder.Append('[')
					            .Append(ServerTypes.ToString(serverType))
					            .Append("] ");

					if (serverType == ServerType.DirectMessage) {
						titleBuilder.Append(channelName);
					}
					else {
						titleBuilder.Append(server.Name)
						            .Append(" - ")
						            .Append(channelName);
					}

					title = titleBuilder.ToString();
				}
				else {
					title = channelName;
				}

				items.Add(new CheckBoxItem<ulong>(channelId) {
					Title = title,
					IsChecked = IncludedChannels == null || IncludedChannels.Contains(channelId)
				});
			}

			return items;
		}

		const string Title = "Included Channels";

		List<CheckBoxItem<ulong>> items;
		try {
			items = await ProgressDialog.ShowIndeterminate(window, Title, "Loading channels...", PrepareChannelItems);
		} catch (Exception e) {
			await Dialog.ShowOk(window, Title, "Error loading channels: " + e.Message);
			return;
		}

		var result = await OpenIdFilterDialog(Title, items);
		if (result != null) {
			IncludedChannels = result;
		}
	}

	public async Task OpenUserFilterDialog() {
		async Task<List<CheckBoxItem<ulong>>> PrepareUserItems(ProgressDialog dialog) {
			var checkBoxItems = new List<CheckBoxItem<ulong>>();

			await foreach (var user in state.Db.Users.Get()) {
				var name = user.Name;
				var discriminator = user.Discriminator;

				checkBoxItems.Add(new CheckBoxItem<ulong>(user.Id) {
					Title = discriminator == null ? name : name + " #" + discriminator,
					IsChecked = IncludedUsers == null || IncludedUsers.Contains(user.Id)
				});
			}

			return checkBoxItems;
		}

		const string Title = "Included Users";

		List<CheckBoxItem<ulong>> items;
		try {
			items = await ProgressDialog.ShowIndeterminate(window, Title, "Loading users...", PrepareUserItems);
		} catch (Exception e) {
			await Dialog.ShowOk(window, Title, "Error loading users: " + e.Message);
			return;
		}

		var result = await OpenIdFilterDialog(Title, items);
		if (result != null) {
			IncludedUsers = result;
		}
	}

	private async Task<HashSet<ulong>?> OpenIdFilterDialog(string title, List<CheckBoxItem<ulong>> items) {
		items.Sort(static (item1, item2) => item1.Title.CompareTo(item2.Title));

		var model = new CheckBoxDialogModel<ulong>(items) {
			Title = title
		};

		var dialog = new CheckBoxDialog { DataContext = model };
		var result = await dialog.ShowDialog<DialogResult.OkCancel>(window);

		return result == DialogResult.OkCancel.Ok ? model.SelectedItems.Select(static item => item.Item).ToHashSet() : null;
	}

	public MessageFilter CreateFilter() {
		MessageFilter filter = new ();

		if (FilterByDate) {
			filter.StartDate = StartDate;
			filter.EndDate = EndDate?.AddDays(1).AddMilliseconds(-1);
		}

		if (FilterByChannel && IncludedChannels != null) {
			filter.ChannelIds = new HashSet<ulong>(IncludedChannels);
		}

		if (FilterByUser && IncludedUsers != null) {
			filter.UserIds = new HashSet<ulong>(IncludedUsers);
		}

		return filter;
	}
}
