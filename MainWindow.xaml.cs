using RadioApp.Models;
using RadioApp.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Data.Entity.Validation;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RadioApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly RadioDatabaseService _databaseService;
        private readonly VlcPlaybackService _playbackService = new VlcPlaybackService();
        private readonly SleepPreventionService _sleepPreventionService = new SleepPreventionService();

        private List<MediaItem> _playlist;
        private Point _dragStartPoint;

        private MediaItem _currentlyPlayingStation;
        private bool _isPlaying;
        private bool _isPaused;
        private bool _isChangingStation;

        // ---- Connectivity / auto-reconnect ----
        private readonly ConnectivityService _connectivityService = new ConnectivityService();

        private DispatcherTimer _connectivityMonitorTimer;   // polls while online to detect drops
        private DispatcherTimer _reconnectCountdownTimer;     // 1s tick: countdown + retry trigger
        private DispatcherTimer _connectedBadgeTimer;         // hides the green "Connected!" badge

        private enum ConnectivityUiState
        {
            Hidden,
            Connecting,
            Connected,
            Reconnecting
        }

        private ConnectivityUiState _connectivityState = ConnectivityUiState.Hidden;
        private bool _isCheckingConnectivity;
        private int _reconnectBackoffStep;
        private int _reconnectSecondsRemaining;
        private MediaItem _stationToResumeAfterReconnect;

        private Brush _connectingBrush;
        private Brush _connectedBrush;
        private Brush _reconnectingBrush;
        private Brush _bufferingBrush;

        private DispatcherTimer _bufferingHideTimer;   // hides "Buffering 100%" after a short delay
        private bool _isBuffering;
        private int _bufferingPercent;

        private MediaItem SelectedStation
        {
            get
            {
                return StationsListBox.SelectedItem as MediaItem;
            }
        }

        private enum StationSortField
        {
            Name,
            PlayCount,
            PlayedTime
        }

        private enum SortDirection
        {
            Ascending,
            Descending
        }

        public MainWindow()
        {
            InitializeComponent();

            _databaseService = new RadioDatabaseService();
            _playlist = new List<MediaItem>();

            InitializeConnectivityTimers();

            Loaded += async (s, e) => await MainWindow_LoadedAsync(s, e);

            _playbackService.PlaybackStarted += PlaybackService_PlaybackStarted;
            _playbackService.PlaybackPaused += PlaybackService_PlaybackPaused;
            _playbackService.PlaybackStopped += PlaybackService_PlaybackStopped;
            _playbackService.PlaybackFailed += PlaybackService_PlaybackFailed;
            _playbackService.NowPlayingTrackChanged += PlaybackService_NowPlayingTrackChanged;
            _playbackService.BufferingProgressChanged += PlaybackService_BufferingProgressChanged;
        }

        private async Task MainWindow_LoadedAsync(object sender, RoutedEventArgs e)
        {
            Log.Information("MainWindow_Loaded started.");

            try
            {
                IsEnabled = false;

                var playlist = await _databaseService.InitializeDatabaseAndGetEnabledMediaItemsAsync();

                _playlist = playlist;

                StationsListBox.ItemsSource = _playlist;

                if (_playlist.Count > 0)
                {
                    StationsListBox.SelectedIndex = 0;
                }
                else
                {
                    Log.Information("Playlist is empty.");
                }

                _ = _playbackService.InitializeAsync();

                // Begin watching internet connectivity (status bar + auto-reconnect).
                _ = StartConnectivityMonitoringAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during MainWindow_Loaded");

                MessageBox.Show(
                    "Error starting the application. Details are logged.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private async void SortStationsByNameAscendingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SortStationsAsync(StationSortField.Name, SortDirection.Ascending);
        }

        private async void SortStationsByNameDescendingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SortStationsAsync(StationSortField.Name, SortDirection.Descending);
        }

        private async void SortStationsByPlayCountAscendingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SortStationsAsync(StationSortField.PlayCount, SortDirection.Ascending);
        }

        private async void SortStationsByPlayCountDescendingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SortStationsAsync(StationSortField.PlayCount, SortDirection.Descending);
        }

        private async void SortStationsByPlayedTimeAscendingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SortStationsAsync(StationSortField.PlayedTime, SortDirection.Ascending);
        }

        private async void SortStationsByPlayedTimeDescendingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SortStationsAsync(StationSortField.PlayedTime, SortDirection.Descending);
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new AddStationWindow
            {
                Owner = this
            };

            bool? result = window.ShowDialog();

            if (result != true)
            {
                return;
            }

            try
            {
                var stream = window.DiscoveredStream;

                string title = !string.IsNullOrWhiteSpace(window.UserTitle)
                                ? window.UserTitle
                                : !string.IsNullOrWhiteSpace(stream.StationName)
                                    ? stream.StationName
                                    : "New radio station";

                string description = !string.IsNullOrWhiteSpace(window.UserDescription)
                    ? window.UserDescription
                    : stream.Description;

                MediaItem savedItem = _databaseService.AddOrUpdateRadioStation(
                    title,
                    description,
                    stream.PageUrl,
                    stream.StreamUrl,
                    stream.Genre
                );

                await ReloadPlaylistAsync();

                var selectedAfterSave = _playlist.FirstOrDefault(x => x.Id == savedItem.Id);

                if (selectedAfterSave != null)
                {
                    StationsListBox.SelectedItem = selectedAfterSave;
                    StationsListBox.ScrollIntoView(selectedAfterSave);
                }
            }
            catch (DbEntityValidationException ex)
            {
                foreach (var validationErrors in ex.EntityValidationErrors)
                {
                    foreach (var validationError in validationErrors.ValidationErrors)
                    {
                        Log.Error(
                            "Entity validation error. Property: {PropertyName}, Error: {ErrorMessage}",
                            validationError.PropertyName,
                            validationError.ErrorMessage
                        );
                    }
                }

                Log.Error(ex, "Failed to add/update station because entity validation failed.");

                MessageBox.Show(
                    "Could not save this station. Details were written to log.",
                    "Save failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to add/update station.");

                MessageBox.Show(
                    "Could not save this station. Details were written to log.",
                    "Save failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private async void DeleteButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            await DeleteSelectedStationWithConfirmationAsync();
        }

        private async void EditButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedStation;

            if (selected == null)
            {
                MessageBox.Show(
                    "Please select a station to edit.",
                    "No station selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                return;
            }

            var window = new AddStationWindow(selected)
            {
                Owner = this
            };

            bool? result = window.ShowDialog();

            if (result != true)
            {
                return;
            }

            try
            {
                MediaItem updatedItem = window.UpdatedItem;

                Log.Information(
                    "Updating station. Id: {Id}, Title: {Title}, StreamUrl: {StreamUrl}",
                    updatedItem.Id,
                    updatedItem.Title,
                    updatedItem.StreamUrl
                );

                _databaseService.UpdateRadioStation(updatedItem);

                int oldIndex = StationsListBox.SelectedIndex;

                await ReloadPlaylistAsync();

                if (_playlist.Count > 0)
                {
                    if (oldIndex < 0)
                    {
                        oldIndex = 0;
                    }

                    if (oldIndex >= _playlist.Count)
                    {
                        oldIndex = _playlist.Count - 1;
                    }

                    StationsListBox.SelectedIndex = oldIndex;
                    StationsListBox.ScrollIntoView(_playlist[oldIndex]);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to update station. Id: {Id}", selected.Id);

                MessageBox.Show(
                    "Could not update this station. Details were written to log.",
                    "Update failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private async void StationsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            MediaItem selected = SelectedStation;

            if (selected == null)
            {
                return;
            }

            await RunPlaybackOperationAsync(async () =>
            {
                await PlayStationAsync(selected);
            });
        }

        private void StationsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void StationsListBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            // Reordering is disabled while a search filter is active: the filtered view's
            // indices don't line up with the underlying playlist, so a drop could scramble
            // the real order.
            if (IsStationFilterActive())
            {
                return;
            }

            Point currentPosition = e.GetPosition(null);

            if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            MediaItem draggedItem = GetMediaItemFromMousePosition(e.GetPosition(StationsListBox));

            if (draggedItem == null)
            {
                return;
            }

            DragDrop.DoDragDrop(
                StationsListBox,
                draggedItem,
                DragDropEffects.Move
            );
        }

        private async void StationsListBox_Drop(object sender, DragEventArgs e)
        {
            // Safety net: dragging shouldn't even start while filtered, but never reorder
            // the playlist from a filtered view just in case a drop still arrives.
            if (IsStationFilterActive())
            {
                return;
            }

            if (!e.Data.GetDataPresent(typeof(MediaItem)))
            {
                return;
            }

            if (!(e.Data.GetData(typeof(MediaItem)) is MediaItem droppedItem))
            {
                return;
            }

            MediaItem targetItem = GetMediaItemFromMousePosition(e.GetPosition(StationsListBox));

            if (targetItem == null)
            {
                return;
            }

            if (droppedItem.Id == targetItem.Id)
            {
                return;
            }

            int oldIndex = _playlist.IndexOf(droppedItem);
            int newIndex = _playlist.IndexOf(targetItem);

            if (oldIndex < 0 || newIndex < 0)
            {
                return;
            }

            _playlist.RemoveAt(oldIndex);
            _playlist.Insert(newIndex, droppedItem);

            await SavePlaylistOrderAsync();

            StationsListBox.ItemsSource = null;
            StationsListBox.ItemsSource = _playlist;
            StationsListBox.SelectedItem = droppedItem;
            StationsListBox.ScrollIntoView(droppedItem);
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            MediaItem selected = SelectedStation;

            if (selected == null)
            {
                Log.Information("Play/Pause button clicked, but no station is selected.");
                return;
            }

            if (_currentlyPlayingStation != null &&
                _currentlyPlayingStation.Id == selected.Id)
            {
                if (_isPlaying)
                {
                    PauseCurrentStation();
                    return;
                }

                if (_isPaused)
                {
                    ResumeCurrentStation();
                    return;
                }
            }

            await RunPlaybackOperationAsync(async () =>
            {
                await PlayStationAsync(selected);
            });
        }

        private async void NextItemButton_Click(object sender, RoutedEventArgs e)
        {
            await SwitchStationAsync(1);
        }

        private async void PreviousItemButton_Click(object sender, RoutedEventArgs e)
        {
            await SwitchStationAsync(-1);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int volume = (int)e.NewValue;

            if (VolumeValueTextBlock != null)
            {
                VolumeValueTextBlock.Text = volume + "%";
            }

            _playbackService.SetVolume(volume);
        }

        private void PlaybackService_PlaybackStarted(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isPlaying = true;
                _isPaused = false;

                _sleepPreventionService.PreventSleep();

                UpdatePlaybackUi();
            });
        }

        private void PlaybackService_PlaybackPaused(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isPlaying = false;
                _isPaused = true;

                _sleepPreventionService.AllowSleep();

                UpdatePlaybackUi();
            });
        }

        private void PlaybackService_PlaybackStopped(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isPlaying = false;
                _isPaused = false;

                _sleepPreventionService.AllowSleep();

                ClearBuffering();
                UpdatePlaybackUi();
            });
        }

        private void PlaybackService_PlaybackFailed(object sender, string message)
        {
            Dispatcher.Invoke(() =>
            {
                ClearBuffering();

                // If the connection just dropped, a frozen stream can also surface here
                // as a hard error. When we are already coordinating a reconnect, swallow
                // the error quietly instead of interrupting the user with a dialog. The
                // Stopped/"Connection lost" event was already recorded in EnterReconnecting,
                // so we don't log another one here.
                if (_connectivityState == ConnectivityUiState.Reconnecting ||
                    _connectivityState == ConnectivityUiState.Connecting)
                {
                    _isPlaying = false;
                    _isPaused = false;

                    _sleepPreventionService.AllowSleep();

                    UpdatePlaybackUi();
                    return;
                }

                // If a station was actually playing before this failure, record that
                // its playback session ended, since PlaybackStopped may not fire reliably
                // (or station id may already be cleared) on hard errors.
                if (_currentlyPlayingStation != null)
                {
                    LogPlaybackEventFireAndForget(
                        _currentlyPlayingStation.Id,
                        PlaybackEventType.Stopped,
                        string.Empty
                    );
                }

                _isPlaying = false;
                _isPaused = false;
                _currentlyPlayingStation = null;

                _sleepPreventionService.AllowSleep();

                UpdatePlaybackUi();

                MessageBox.Show(
                    message,
                    "Playback failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            });
        }

        private void PlaybackService_NowPlayingTrackChanged(object sender, string trackTitle)
        {
            Dispatcher.Invoke(() =>
            {
                NowPlayingTrackTextBlock.Text = "♪ " + trackTitle;

                if (_currentlyPlayingStation != null)
                {
                    LogPlaybackEventFireAndForget(
                        _currentlyPlayingStation.Id,
                        PlaybackEventType.TrackChanged,
                        trackTitle
                    );
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _connectivityMonitorTimer?.Stop();
                _reconnectCountdownTimer?.Stop();
                _connectedBadgeTimer?.Stop();

                _playbackService.PlaybackStarted -= PlaybackService_PlaybackStarted;
                _playbackService.PlaybackPaused -= PlaybackService_PlaybackPaused;
                _playbackService.PlaybackStopped -= PlaybackService_PlaybackStopped;
                _playbackService.PlaybackFailed -= PlaybackService_PlaybackFailed;
                _playbackService.NowPlayingTrackChanged -= PlaybackService_NowPlayingTrackChanged;
                _playbackService.BufferingProgressChanged -= PlaybackService_BufferingProgressChanged;

                _sleepPreventionService.AllowSleep();

                StopCurrentStation();

                _playbackService.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error while closing VLC playback service.");
            }

            base.OnClosed(e);
        }

        private void SetPlaybackOperationUiBusy(bool isBusy)
        {
            _isChangingStation = isBusy;

            PlayPauseButton.IsEnabled = !isBusy;
            NextItemButton.IsEnabled = !isBusy;
            PreviousItemButton.IsEnabled = !isBusy;
            AddButton.IsEnabled = !isBusy;
            EditButton.IsEnabled = !isBusy;
            DeleteButton.IsEnabled = !isBusy;
            StationsListBox.IsEnabled = !isBusy;

            Mouse.OverrideCursor = isBusy ? Cursors.Wait : null;
        }

        private async Task RunPlaybackOperationAsync(Func<Task> operation)
        {
            if (_isChangingStation)
            {
                return;
            }

            try
            {
                SetPlaybackOperationUiBusy(true);

                await operation();
            }
            finally
            {
                SetPlaybackOperationUiBusy(false);
            }
        }

        private async Task PlayStationAsync(MediaItem station, string startedComment = "")
        {
            if (station == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(station.StreamUrl))
            {
                MessageBox.Show(
                    "Selected station does not have Stream URL.",
                    "Cannot play station",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );

                return;
            }

            // If another station is currently playing, record that its session ended
            // before we start the new one.
            if (_currentlyPlayingStation != null && _currentlyPlayingStation.Id != station.Id)
            {
                LogPlaybackEventFireAndForget(
                    _currentlyPlayingStation.Id,
                    PlaybackEventType.Stopped,
                    string.Empty
                );
            }

            // Clear the previous station's track title immediately so stale info
            // isn't shown while the new stream's metadata hasn't arrived yet.
            NowPlayingTrackTextBlock.Text = "";

            try
            {
                Log.Information(
                    "Starting playback. StationId: {StationId}, Title: {Title}, StreamUrl: {StreamUrl}",
                    station.Id,
                    station.Title,
                    station.StreamUrl
                );

                await _playbackService.PlayAsync(station.StreamUrl);

                _playbackService.SetVolume((int)VolumeSlider.Value);

                await _databaseService.IncrementPlayCountAsync(station.Id);
                station.PlayCount++;

                _currentlyPlayingStation = station;
                _isPlaying = true;
                _isPaused = false;

                LogPlaybackEventFireAndForget(
                    station.Id,
                    PlaybackEventType.Started,
                    startedComment
                );

                UpdatePlaybackUi();
            }
            catch (Exception ex)
            {
                _isPlaying = false;
                _isPaused = false;
                _currentlyPlayingStation = null;

                UpdatePlaybackUi();

                Log.Error(
                    ex,
                    "Failed to start playback. StationId: {StationId}, Title: {Title}, StreamUrl: {StreamUrl}",
                    station.Id,
                    station.Title,
                    station.StreamUrl
                );

                MessageBox.Show(
                    "Could not play this station. Details were written to log.",
                    "Playback failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void PauseCurrentStation()
        {
            if (!_isPlaying || _currentlyPlayingStation == null)
            {
                return;
            }

            try
            {
                _playbackService.Pause();

                _isPlaying = false;
                _isPaused = true;

                Log.Information(
                    "Playback paused. StationId: {StationId}, Title: {Title}",
                    _currentlyPlayingStation.Id,
                    _currentlyPlayingStation.Title
                );

                UpdatePlaybackUi();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to pause playback.");
            }
        }

        private void ResumeCurrentStation()
        {
            if (!_isPaused || _currentlyPlayingStation == null)
            {
                return;
            }

            try
            {
                _playbackService.Resume();

                _isPlaying = true;
                _isPaused = false;

                Log.Information(
                    "Playback resumed. StationId: {StationId}, Title: {Title}",
                    _currentlyPlayingStation.Id,
                    _currentlyPlayingStation.Title
                );

                UpdatePlaybackUi();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to resume playback.");
            }
        }

        private void StopCurrentStation()
        {
            try
            {
                if (_currentlyPlayingStation != null)
                {
                    LogPlaybackEventFireAndForget(
                        _currentlyPlayingStation.Id,
                        PlaybackEventType.Stopped,
                        string.Empty
                    );
                }

                _playbackService.Stop();

                _isPlaying = false;
                _isPaused = false;
                _currentlyPlayingStation = null;

                Log.Information("Playback stopped.");

                UpdatePlaybackUi();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to stop current station.");
            }
        }

        /// <summary>
        /// Fires off a play-history log write without blocking or awaiting it from the
        /// UI thread. Any failure is logged but never surfaced to the user, since play
        /// history is a non-critical, best-effort feature.
        /// </summary>
        private void LogPlaybackEventFireAndForget(int mediaItemId, PlaybackEventType eventType, string trackName)
        {
            _ = LogPlaybackEventSafeAsync(mediaItemId, eventType, trackName);
        }

        private async Task LogPlaybackEventSafeAsync(int mediaItemId, PlaybackEventType eventType, string trackName)
        {
            try
            {
                await _databaseService.LogPlaybackEventAsync(mediaItemId, eventType, trackName);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "Failed to log playback event. MediaItemId: {MediaItemId}, EventType: {EventType}",
                    mediaItemId,
                    eventType
                );
            }
        }

        /// <summary>
        /// Shared deletion logic for the Delete button and the right-click context menu.
        /// Asks the user to confirm, deletes the selected station, and reloads the playlist.
        /// </summary>
        private async Task DeleteSelectedStationWithConfirmationAsync()
        {
            MediaItem selected = SelectedStation;

            if (selected == null)
            {
                return;
            }

            MessageBoxResult choice = MessageBox.Show(
                $"Delete \"{selected.Title}\"?",
                "Confirm delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (choice != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                _databaseService.DeleteMediaItem(selected.Id);

                Log.Information(
                    "Station deleted. Id: {Id}, Title: {Title}",
                    selected.Id,
                    selected.Title
                );

                _playlist = await _databaseService.GetEnabledMediaItems();
                //StationsListBox.ItemsSource = _playlist;
                RefreshStationsView();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to delete station. Id: {Id}", selected.Id);

                MessageBox.Show(
                    "Could not delete this station. Details were written to log.",
                    "Delete failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private async Task ReloadPlaylistAsync()
        {
            _playlist = await _databaseService.InitializeDatabaseAndGetEnabledMediaItemsAsync();

            StationsListBox.ItemsSource = null;
            StationsListBox.ItemsSource = _playlist;

            Log.Information("Playlist reloaded. Items count: {Count}", _playlist.Count);
        }

        private void UpdatePlaybackUi()
        {
            if (_currentlyPlayingStation == null)
            {
                PlayPauseButton.Content = "▶";
                NowPlayingTextBlock.Text = "Now playing:";
                NowPlayingTrackTextBlock.Text = "";
                NowPlayingGenreTextBlock.Text = "";
                return;
            }

            if (_isPlaying)
            {
                PlayPauseButton.Content = "⏸";
                NowPlayingTextBlock.Text = "Now playing: " + _currentlyPlayingStation.Title;
                NowPlayingGenreTextBlock.Text = string.IsNullOrWhiteSpace(_currentlyPlayingStation.Genre)
                    ? ""
                    : "Genre: " + _currentlyPlayingStation.Genre;
                return;
            }

            if (_isPaused)
            {
                PlayPauseButton.Content = "▶";
                NowPlayingTextBlock.Text = "Paused: " + _currentlyPlayingStation.Title;
                return;
            }

            PlayPauseButton.Content = "▶";
            NowPlayingTextBlock.Text = "Now playing:";
            NowPlayingTrackTextBlock.Text = "";
            NowPlayingGenreTextBlock.Text = "";
        }

        private async Task SwitchStationAsync(int direction)
        {
            int stationCount = StationsListBox.Items.Count;

            if (stationCount == 0)
            {
                return;
            }

            int currentIndex = StationsListBox.SelectedIndex;
            int newIndex;

            if (currentIndex < 0)
            {
                newIndex = direction > 0 ? 0 : stationCount - 1;
            }
            else
            {
                newIndex = currentIndex + direction;

                if (newIndex >= stationCount)
                {
                    newIndex = 0;
                }
                else if (newIndex < 0)
                {
                    newIndex = stationCount - 1;
                }
            }

            StationsListBox.SelectedIndex = newIndex;
            StationsListBox.ScrollIntoView(StationsListBox.SelectedItem);

            MediaItem selected = SelectedStation;

            if (selected == null)
            {
                return;
            }

            await RunPlaybackOperationAsync(async () =>
            {
                await PlayStationAsync(selected);
            });
        }

        private MediaItem GetMediaItemFromMousePosition(Point point)
        {
            DependencyObject element = StationsListBox.InputHitTest(point) as DependencyObject;

            while (element != null)
            {
                ListBoxItem listBoxItem = element as ListBoxItem;

                if (listBoxItem != null)
                {
                    return listBoxItem.DataContext as MediaItem;
                }

                element = VisualTreeHelper.GetParent(element);
            }

            return null;
        }

        private async Task SavePlaylistOrderAsync()
        {
            for (int i = 0; i < _playlist.Count; i++)
            {
                _playlist[i].SortOrder = i;
            }

            await _databaseService.UpdateSortOrderAsync(_playlist);

            Log.Information(
                "Playlist order saved. Items count: {Count}",
                _playlist.Count
            );
        }

        private async Task SortStationsAsync(StationSortField field, SortDirection direction)
        {
            if (_playlist == null || _playlist.Count == 0)
            {
                return;
            }

            MediaItem selectedItem = StationsListBox.SelectedItem as MediaItem;

            // Played-time sorting needs aggregated totals from the StationTotalPlayTime
            // view. Fetch them once up front (stations with no sessions default to 0).
            Dictionary<int, long> playedSeconds = null;
            if (field == StationSortField.PlayedTime)
            {
                playedSeconds = await _databaseService.GetStationTotalPlaySecondsAsync();
            }

            switch (field)
            {
                case StationSortField.Name:
                    _playlist = direction == SortDirection.Ascending
                        ? _playlist
                            .OrderBy(x => x.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                            .ToList()
                        : _playlist
                            .OrderByDescending(x => x.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                    break;

                case StationSortField.PlayCount:
                    _playlist = direction == SortDirection.Ascending
                        ? _playlist
                            .OrderBy(x => x.PlayCount)
                            .ThenBy(x => x.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                            .ToList()
                        : _playlist
                            .OrderByDescending(x => x.PlayCount)
                            .ThenBy(x => x.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                    break;

                case StationSortField.PlayedTime:
                    var totals = playedSeconds ?? new Dictionary<int, long>();

                    Func<MediaItem, long> getSeconds = m =>
                    {
                        long s;
                        return totals.TryGetValue(m.Id, out s) ? s : 0L;
                    };

                    _playlist = direction == SortDirection.Ascending
                        ? _playlist
                            .OrderBy(getSeconds)
                            .ThenBy(x => x.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                            .ToList()
                        : _playlist
                            .OrderByDescending(getSeconds)
                            .ThenBy(x => x.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                    break;

                default:
                    throw new InvalidOperationException("Unsupported station sort field: " + field);
            }

            await ApplyPlaylistOrderAsync(selectedItem);

            Log.Information(
                "Playlist sorted. Field: {Field}, Direction: {Direction}, Items count: {Count}",
                field,
                direction,
                _playlist.Count
            );
        }

        private async Task ApplyPlaylistOrderAsync(MediaItem selectedItem)
        {
            for (int i = 0; i < _playlist.Count; i++)
            {
                _playlist[i].SortOrder = i;
            }

            await _databaseService.UpdateSortOrderAsync(_playlist);

            StationsListBox.ItemsSource = null;
            StationsListBox.ItemsSource = _playlist;

            if (selectedItem == null)
            {
                return;
            }

            MediaItem selectedAfterSort = _playlist.FirstOrDefault(x => x.Id == selectedItem.Id);

            if (selectedAfterSort != null)
            {
                StationsListBox.SelectedItem = selectedAfterSort;
                StationsListBox.ScrollIntoView(selectedAfterSort);
            }
        }

        private void StationsListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Select the item under the right-click so context menu actions target it
            MediaItem clickedItem = GetMediaItemFromMousePosition(e.GetPosition(StationsListBox));

            if (clickedItem != null)
            {
                StationsListBox.SelectedItem = clickedItem;
            }
        }

        private async void ContextPlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MediaItem selected = SelectedStation;

            if (selected == null)
                return;

            await RunPlaybackOperationAsync(async () =>
            {
                await PlayStationAsync(selected);
            });
        }

        private void ContextEditMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Reuse the same logic as the Edit button
            EditButton_Click(sender, e);
        }

        private async void ContextDeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await DeleteSelectedStationWithConfirmationAsync();
        }

        private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Don't hijack keys while the user is typing into a text box anywhere
            // in the window (defensive, even though MainWindow currently has none).
            if (e.OriginalSource is TextBox)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Delete:
                    e.Handled = true;
                    await DeleteSelectedStationWithConfirmationAsync();
                    break;

                case Key.Space:
                    e.Handled = true;
                    TogglePlayPauseFromShortcut();
                    break;

                case Key.Right:
                    e.Handled = true;
                    await SwitchStationAsync(1);
                    break;

                case Key.Left:
                    e.Handled = true;
                    await SwitchStationAsync(-1);
                    break;

                case Key.OemPlus:
                case Key.Add:
                    e.Handled = true;
                    AdjustVolume(5);
                    break;

                case Key.OemMinus:
                case Key.Subtract:
                    e.Handled = true;
                    AdjustVolume(-5);
                    break;
            }
        }

        private void TogglePlayPauseFromShortcut()
        {
            MediaItem selected = SelectedStation;

            if (selected == null)
            {
                return;
            }

            if (_currentlyPlayingStation != null &&
                _currentlyPlayingStation.Id == selected.Id)
            {
                if (_isPlaying)
                {
                    PauseCurrentStation();
                    return;
                }

                if (_isPaused)
                {
                    ResumeCurrentStation();
                    return;
                }
            }

            _ = RunPlaybackOperationAsync(async () =>
            {
                await PlayStationAsync(selected);
            });
        }

        private void AdjustVolume(int delta)
        {
            double newValue = VolumeSlider.Value + delta;

            if (newValue < VolumeSlider.Minimum)
            {
                newValue = VolumeSlider.Minimum;
            }

            if (newValue > VolumeSlider.Maximum)
            {
                newValue = VolumeSlider.Maximum;
            }

            VolumeSlider.Value = newValue;
            // VolumeSlider_ValueChanged already updates the label and calls SetVolume
        }

        // ---- Station search / live filter ----

        private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchTextBox.Visibility == Visibility.Visible)
            {
                // Toggle off: hide the box and restore the full list.
                SearchTextBox.Visibility = Visibility.Collapsed;
                SearchTextBox.Text = string.Empty;
                StationsListBox.ItemsSource = _playlist;
            }
            else
            {
                // Toggle on: show and focus so the user can start typing right away.
                SearchTextBox.Visibility = Visibility.Visible;
                SearchTextBox.Focus();
            }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyStationFilter(SearchTextBox.Text);
        }

        /// <summary>
        /// Filters the stations list by title as the user types. An empty query restores
        /// the full list. The filtered entries are the same MediaItem instances, so
        /// selection and playback keep working normally.
        /// </summary>
        private void ApplyStationFilter(string query)
        {
            if (_playlist == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                StationsListBox.ItemsSource = _playlist;
                return;
            }

            string trimmed = query.Trim();

            var filtered = _playlist
                .Where(x => !string.IsNullOrEmpty(x.Title) &&
                            x.Title.IndexOf(trimmed, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .ToList();

            StationsListBox.ItemsSource = filtered;
        }

        /// <summary>
        /// True when the search box is open and contains a non-empty query, i.e. the list
        /// is currently showing filtered results rather than the full playlist.
        /// </summary>
        private bool IsStationFilterActive()
        {
            return SearchTextBox != null
                && SearchTextBox.Visibility == Visibility.Visible
                && !string.IsNullOrWhiteSpace(SearchTextBox.Text);
        }

        /// <summary>
        /// Rebinds the stations list, keeping the active search filter applied if there is
        /// one — so operations like deleting a station don't clear the current results.
        /// </summary>
        private void RefreshStationsView()
        {
            if (IsStationFilterActive())
            {
                ApplyStationFilter(SearchTextBox.Text);
            }
            else
            {
                StationsListBox.ItemsSource = null;
                StationsListBox.ItemsSource = _playlist;
            }
        }

        // ---- Station tooltip: play count + total listening time, read live on hover ----

        private void StationTooltip_Opening(object sender, ToolTipEventArgs e)
        {
            var element = sender as FrameworkElement;
            var station = element?.DataContext as MediaItem;
            var toolTip = element?.ToolTip as ToolTip;

            if (station == null || toolTip == null)
            {
                return;
            }

            long totalSeconds = 0;

            try
            {
                // Read on demand so the number reflects the current database state each
                // time the tooltip is shown, for just this one station.
                totalSeconds = _databaseService.GetStationTotalPlaySeconds(station.Id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read total play time for station {Id}.", station.Id);
            }

            toolTip.Content = BuildStationTooltipContent(station, totalSeconds);
        }

        private static object BuildStationTooltipContent(MediaItem station, long totalSeconds)
        {
            string timesWord = station.PlayCount == 1 ? "time" : "times";
            string stats = "Played " + station.PlayCount + " " + timesWord
                         + " · total " + FormatDuration(totalSeconds);

            var panel = new StackPanel { MaxWidth = 300 };

            panel.Children.Add(new TextBlock
            {
                Text = stats,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(station.Description))
            {
                panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });

                panel.Children.Add(new TextBlock
                {
                    Text = station.Description,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            return panel;
        }

        private static string FormatDuration(long totalSeconds)
        {
            if (totalSeconds < 0)
            {
                totalSeconds = 0;
            }

            long hours = totalSeconds / 3600;
            long minutes = (totalSeconds % 3600) / 60;
            long seconds = totalSeconds % 60;

            return string.Format("{0:00}:{1:00}:{2:00}", hours, minutes, seconds);
        }

        // ============================================================
        //  Connectivity status + automatic reconnect
        // ============================================================
        //
        //  Two concerns are kept separate:
        //    (a) Is there internet at all? -> drives the status bar text/colour.
        //    (b) Was a station playing when it dropped? -> drives auto-resume.
        //
        //  Detection is by ACTIVE probing (ConnectivityService), because LibVLC
        //  often just stalls silently on a network drop without raising an error.
        //
        //  States:
        //    Connecting   - amber "Connecting to the internet..." (startup, or
        //                   offline while nothing was playing). Retries silently.
        //    Connected    - green "Connected!" shown for 5s, then hidden.
        //    Reconnecting - red "Reconnect in X seconds" + "Connect now" button,
        //                   shown when the connection drops mid-playback. Backoff
        //                   schedule 3, 6, 9, 12, 15, 15, ... seconds (unbounded),
        //                   auto-resumes the last station once back online.
        // ============================================================

        private void InitializeConnectivityTimers()
        {
            var connecting = new SolidColorBrush(Color.FromRgb(0xC8, 0x96, 0x00)); // amber
            connecting.Freeze();
            _connectingBrush = connecting;

            var connected = new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57));  // green
            connected.Freeze();
            _connectedBrush = connected;

            var reconnecting = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)); // red
            reconnecting.Freeze();
            _reconnectingBrush = reconnecting;

            _connectivityMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(6)
            };
            _connectivityMonitorTimer.Tick += ConnectivityMonitorTimer_Tick;

            _reconnectCountdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _reconnectCountdownTimer.Tick += ReconnectCountdownTimer_Tick;

            _connectedBadgeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _connectedBadgeTimer.Tick += ConnectedBadgeTimer_Tick;

            var buffering = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x8C)); // dark blue
            buffering.Freeze();
            _bufferingBrush = buffering;

            _bufferingHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _bufferingHideTimer.Tick += BufferingHideTimer_Tick;
        }

        private async Task StartConnectivityMonitoringAsync()
        {
            SetConnectivityStatus(ConnectivityUiState.Connecting);

            bool online = await CheckInternetSafeAsync();

            if (online)
            {
                EnterConnectedState();
            }
            else
            {
                EnterConnectingRetry();
            }
        }

        /// <summary>
        /// Runs a connectivity probe with a guard flag so overlapping checks (from
        /// the monitor tick, the countdown tick, and the "Connect now" button) never
        /// stack up. Never throws.
        /// </summary>
        private async Task<bool> CheckInternetSafeAsync()
        {
            _isCheckingConnectivity = true;

            try
            {
                return await _connectivityService.CheckInternetAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Connectivity check failed unexpectedly.");
                return false;
            }
            finally
            {
                _isCheckingConnectivity = false;
            }
        }

        private void StartConnectivityMonitor()
        {
            if (_connectivityMonitorTimer != null && !_connectivityMonitorTimer.IsEnabled)
            {
                _connectivityMonitorTimer.Start();
            }
        }

        private void StopConnectivityMonitor()
        {
            _connectivityMonitorTimer?.Stop();
        }

        private async void ConnectivityMonitorTimer_Tick(object sender, EventArgs e)
        {
            // Only meaningful when we believe we are online.
            if (_connectivityState == ConnectivityUiState.Reconnecting ||
                _connectivityState == ConnectivityUiState.Connecting)
            {
                return;
            }

            if (_isCheckingConnectivity)
            {
                return;
            }

            bool online = await CheckInternetSafeAsync();

            if (online)
            {
                return;
            }

            StopConnectivityMonitor();

            if (_currentlyPlayingStation != null && _isPlaying)
            {
                EnterReconnecting(_currentlyPlayingStation);
            }
            else
            {
                EnterConnectingRetry();
            }
        }

        private void EnterConnectedState()
        {
            _reconnectCountdownTimer.Stop();
            _reconnectBackoffStep = 0;

            SetConnectivityStatus(ConnectivityUiState.Connected);

            // Show the green badge briefly, then hide it.
            _connectedBadgeTimer.Stop();
            _connectedBadgeTimer.Start();

            // Keep watching for future drops.
            StartConnectivityMonitor();
        }

        private void ConnectedBadgeTimer_Tick(object sender, EventArgs e)
        {
            _connectedBadgeTimer.Stop();

            if (_connectivityState == ConnectivityUiState.Connected)
            {
                SetConnectivityStatus(ConnectivityUiState.Hidden);
            }
        }

        private void EnterConnectingRetry()
        {
            StopConnectivityMonitor();

            _stationToResumeAfterReconnect = null;
            _reconnectBackoffStep = 0;

            SetConnectivityStatus(ConnectivityUiState.Connecting);

            _reconnectSecondsRemaining = 5;

            if (!_reconnectCountdownTimer.IsEnabled)
            {
                _reconnectCountdownTimer.Start();
            }
        }

        private void EnterReconnecting(MediaItem station)
        {
            StopConnectivityMonitor();

            _stationToResumeAfterReconnect = station;
            _reconnectBackoffStep = 0;
            _reconnectSecondsRemaining = Math.Min(3 * (_reconnectBackoffStep + 1), 15); // 3

            SetConnectivityStatus(ConnectivityUiState.Reconnecting, _reconnectSecondsRemaining);

            if (!_reconnectCountdownTimer.IsEnabled)
            {
                _reconnectCountdownTimer.Start();
            }

            // Record the interruption in play history (Stopped + reason) and in the log.
            // This is the only place the silent network drop gets persisted, since VLC
            // frequently stalls without raising an error of its own.
            LogPlaybackEventFireAndForget(
                station.Id,
                PlaybackEventType.Stopped,
                "Connection lost / No internet connection"
            );

            Log.Warning(
                "Internet connection lost while playing station {Id} ({Title}). Reconnecting...",
                station.Id,
                station.Title
            );
        }

        private async void ReconnectCountdownTimer_Tick(object sender, EventArgs e)
        {
            if (_reconnectSecondsRemaining > 1)
            {
                _reconnectSecondsRemaining--;

                if (_connectivityState == ConnectivityUiState.Reconnecting)
                {
                    SetConnectivityStatus(ConnectivityUiState.Reconnecting, _reconnectSecondsRemaining);
                }

                return;
            }

            // Countdown elapsed -> attempt to reconnect now.
            _reconnectSecondsRemaining = 0;
            await AttemptReconnectAsync();
        }

        private void ConnectNowButton_Click(object sender, RoutedEventArgs e)
        {
            Log.Information("Manual 'Connect now' requested.");
            _reconnectSecondsRemaining = 0;
            _ = AttemptReconnectAsync();
        }

        private async Task AttemptReconnectAsync()
        {
            if (_isCheckingConnectivity)
            {
                // A probe is already in flight; retry on the next tick.
                _reconnectSecondsRemaining = 1;

                if (!_reconnectCountdownTimer.IsEnabled)
                {
                    _reconnectCountdownTimer.Start();
                }

                return;
            }

            ConnectNowButton.IsEnabled = false;

            bool online = await CheckInternetSafeAsync();

            if (online)
            {
                _reconnectCountdownTimer.Stop();

                bool wasReconnecting = _connectivityState == ConnectivityUiState.Reconnecting;
                MediaItem station = _stationToResumeAfterReconnect;
                _stationToResumeAfterReconnect = null;

                EnterConnectedState();

                if (wasReconnecting && station != null)
                {
                    Log.Information(
                        "Internet restored. Auto-resuming station {Id} ({Title}).",
                        station.Id,
                        station.Title
                    );

                    await RunPlaybackOperationAsync(async () =>
                    {
                        await PlayStationAsync(station, "Connection restored");
                    });
                }

                return;
            }

            // Still offline -> schedule the next wait.
            if (_connectivityState == ConnectivityUiState.Reconnecting)
            {
                _reconnectBackoffStep++;
                int delay = Math.Min(3 * (_reconnectBackoffStep + 1), 15); // 3,6,9,12,15,15,...
                _reconnectSecondsRemaining = delay;
                SetConnectivityStatus(ConnectivityUiState.Reconnecting, _reconnectSecondsRemaining);
            }
            else
            {
                _reconnectSecondsRemaining = 5;
                SetConnectivityStatus(ConnectivityUiState.Connecting);
            }

            if (!_reconnectCountdownTimer.IsEnabled)
            {
                _reconnectCountdownTimer.Start();
            }
        }

        private void SetConnectivityStatus(ConnectivityUiState state, int secondsRemaining = 0)
        {
            _connectivityState = state;

            if (state == ConnectivityUiState.Reconnecting)
            {
                _reconnectSecondsRemaining = secondsRemaining;
            }

            RenderStatusBar();
        }

        // ---- Station buffering display (shares the same status bar) ----

        private void PlaybackService_BufferingProgressChanged(object sender, double cache)
        {
            Dispatcher.Invoke(() => UpdateBuffering(cache));
        }

        private void UpdateBuffering(double cache)
        {
            int percent = (int)Math.Round(cache);

            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            _bufferingPercent = percent;

            // Buffering must never override an active connectivity message
            // (Connecting / Reconnecting). Track the value but don't show it now.
            if (_connectivityState == ConnectivityUiState.Connecting ||
                _connectivityState == ConnectivityUiState.Reconnecting)
            {
                return;
            }

            _isBuffering = true;

            // While buffering is still in progress, cancel any pending auto-hide.
            _bufferingHideTimer.Stop();

            // Once fully buffered, keep "Buffering 100%" visible briefly, then hide.
            if (percent >= 100)
            {
                _bufferingHideTimer.Start();
            }

            RenderStatusBar();
        }

        private void BufferingHideTimer_Tick(object sender, EventArgs e)
        {
            _bufferingHideTimer.Stop();
            _isBuffering = false;
            RenderStatusBar();
        }

        private void ClearBuffering()
        {
            _bufferingHideTimer.Stop();
            _isBuffering = false;
            RenderStatusBar();
        }

        /// <summary>
        /// Single source of truth for the status bar. Priority (high to low):
        /// Reconnecting > Connecting > Buffering > Connected > Hidden. Every state
        /// change updates a field and calls this, so the two features (connectivity
        /// and buffering) never clobber each other.
        /// </summary>
        private void RenderStatusBar()
        {
            if (_connectivityState == ConnectivityUiState.Reconnecting)
            {
                ConnectivityStatusPanel.Visibility = Visibility.Visible;
                ConnectivityStatusTextBlock.Foreground = _reconnectingBrush;
                ConnectivityStatusTextBlock.Text = _reconnectSecondsRemaining == 1
                    ? "Reconnect in 1 second"
                    : "Reconnect in " + _reconnectSecondsRemaining + " seconds";
                ConnectNowButton.Visibility = Visibility.Visible;
                ConnectNowButton.IsEnabled = true;
                return;
            }

            if (_connectivityState == ConnectivityUiState.Connecting)
            {
                ConnectivityStatusPanel.Visibility = Visibility.Visible;
                ConnectivityStatusTextBlock.Foreground = _connectingBrush;
                ConnectivityStatusTextBlock.Text = "Connecting to the internet...";
                ConnectNowButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (_isBuffering)
            {
                ConnectivityStatusPanel.Visibility = Visibility.Visible;
                ConnectivityStatusTextBlock.Foreground = _bufferingBrush;
                ConnectivityStatusTextBlock.Text = "Buffering " + _bufferingPercent + "%";
                ConnectNowButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (_connectivityState == ConnectivityUiState.Connected)
            {
                ConnectivityStatusPanel.Visibility = Visibility.Visible;
                ConnectivityStatusTextBlock.Foreground = _connectedBrush;
                ConnectivityStatusTextBlock.Text = "Connected!";
                ConnectNowButton.Visibility = Visibility.Collapsed;
                return;
            }

            ConnectivityStatusPanel.Visibility = Visibility.Collapsed;
            ConnectNowButton.Visibility = Visibility.Collapsed;
        }
    }
}