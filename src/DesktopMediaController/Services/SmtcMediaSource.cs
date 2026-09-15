using System.IO;
using System.Windows.Media.Imaging;
using DesktopMediaController.Core;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DesktopMediaController.Services;

/// <summary>
/// The widget's window onto Windows SMTC (System Media Transport Controls): reads what is playing and
/// sends play/pause/skip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polled, not event-driven, and that is deliberate.</b> The obvious design subscribes to
/// <c>MediaPropertiesChanged</c> / <c>PlaybackInfoChanged</c> / <c>TimelinePropertiesChanged</c> on
/// every session and pushes into the UI. Those events are raised on threads of WinRT's choosing, they
/// arrive far faster than the screen refreshes (a player ticking its position can fire several times
/// a second), and every one of them would need marshalling onto the dispatcher — an event storm
/// aimed straight at the UI thread, which is exactly what the spec says must not happen. Polling on a
/// timer instead makes the UI thread the only caller, caps the work at a fixed rate no matter what
/// the players do, and removes the whole class of cross-thread bugs.
/// </para>
/// <para>
/// The expensive call (<c>TryGetMediaPropertiesAsync</c>, plus album-art decode) only runs when the
/// track identity actually changes; the per-tick path is just a few cheap synchronous reads.
/// </para>
/// </remarks>
internal sealed class SmtcMediaSource : IDisposable
{
    private readonly SessionPicker _picker;
    private readonly SemaphoreSlim _oneRefreshAtATime = new(1, 1);
    private readonly object _snapshotGate = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private MediaSnapshot _snapshot = MediaSnapshot.Idle;

    // Cache for the costly part. Keyed by track identity, so a position tick never re-decodes art.
    private string? _mediaKey;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private BitmapImage? _cover;

    private bool _disposed;

    /// <summary>
    /// Creates the source. <paramref name="filter"/> decides which players the automatic choice is
    /// allowed to land on; every call that resolves a session goes through the single picker built
    /// here, so the automatic choice, the transport buttons and manual cycling can never disagree
    /// about which session is current — or about what a returned index means.
    /// </summary>
    public SmtcMediaSource(MediaSourceFilter? filter = null) => _picker = new SessionPicker(filter);

    /// <summary>The most recent reading. Cheap and thread-safe; the UI calls this on every repaint.</summary>
    public MediaSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate) return _snapshot;
        }
    }

    /// <summary>Connects to SMTC. Safe to call once; failures leave the widget in its idle state.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            // Without SMTC the widget can still exist and be dragged; it just has nothing to show.
            CrashLog.Write("smtc-init", ex);
        }
    }

    /// <summary>
    /// Re-reads the world and publishes a new <see cref="Snapshot"/>. Single-flight: if a refresh is
    /// already running, this returns immediately rather than queueing up behind it — a slow player
    /// must not be able to build a backlog of pending refreshes.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed || _manager is null) return;
        if (!await _oneRefreshAtATime.WaitAsync(0).ConfigureAwait(true)) return;

        try
        {
            var sessions = _manager.GetSessions();
            var infos = new MediaSessionInfo[sessions.Count];
            for (var i = 0; i < sessions.Count; i++) infos[i] = ReadSession(sessions[i]);

            var chosen = _picker.Choose(infos);
            if (chosen is null)
            {
                _mediaKey = null;
                _cover = null;
                Publish(MediaSnapshot.Idle);
                return;
            }

            var session = sessions[chosen.Value];
            var info = infos[chosen.Value];
            await LoadTrackIfChangedAsync(session, info).ConfigureAwait(true);

            Publish(new MediaSnapshot(
                info with { Title = _title, Artist = _artist, Album = _album },
                _cover));
        }
        catch (Exception ex)
        {
            CrashLog.Write("smtc-refresh", ex);
        }
        finally
        {
            _oneRefreshAtATime.Release();
        }
    }

    // ---- transport ---------------------------------------------------------------------------

    /// <summary>Play/pause the displayed session. False when nothing is displayed or the player refused.</summary>
    public Task<bool> TogglePlayPauseAsync() => OnCurrentSessionAsync(s => s.TryTogglePlayPauseAsync());

    public Task<bool> NextAsync() => OnCurrentSessionAsync(s => s.TrySkipNextAsync());

    public Task<bool> PreviousAsync() => OnCurrentSessionAsync(s => s.TrySkipPreviousAsync());

    /// <summary>
    /// Moves to the next usable session. The automatic choice can be wrong when two players are both
    /// playing, and only the user knows which one they meant.
    /// </summary>
    public void CycleSession()
    {
        if (_manager is null) return;

        try
        {
            var sessions = _manager.GetSessions();
            var infos = sessions.Select(ReadSession).ToArray();
            // Resolve the pick before dropping the cache: the cover must follow the new session.
            if (_picker.ChooseNext(infos) is not null) _mediaKey = null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("smtc-cycle", ex);
        }
    }

    private async Task<bool> OnCurrentSessionAsync(
        Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> action)
    {
        if (_manager is null) return false;

        try
        {
            var sessions = _manager.GetSessions();
            var infos = sessions.Select(ReadSession).ToArray();
            var chosen = _picker.Choose(infos);
            if (chosen is null) return false;

            var ok = await action(sessions[chosen.Value]);
            // Reflect the new state immediately instead of waiting for the next poll, so a button
            // press feels like it did something even if the player is slow to update its timeline.
            await RefreshAsync();
            return ok;
        }
        catch (Exception ex)
        {
            CrashLog.Write("smtc-command", ex);
            return false;
        }
    }

    // ---- reading -----------------------------------------------------------------------------

    private static MediaSessionInfo ReadSession(GlobalSystemMediaTransportControlsSession session)
    {
        var info = new MediaSessionInfo
        {
            SourceId = session.SourceAppUserModelId ?? string.Empty,
        };

        try
        {
            if (session.GetPlaybackInfo() is { } playback)
            {
                info = info with
                {
                    Status = ToStatus(playback.PlaybackStatus),
                    CanPlay = playback.Controls?.IsPlayEnabled ?? false,
                    CanPause = playback.Controls?.IsPauseEnabled ?? false,
                    CanNext = playback.Controls?.IsNextEnabled ?? false,
                    CanPrevious = playback.Controls?.IsPreviousEnabled ?? false,
                    CanSeek = playback.Controls?.IsPlaybackPositionEnabled ?? false,
                };
            }
        }
        catch (Exception)
        {
            // A player mid-shutdown can fail any of these; the track still shows, minus its controls.
        }

        try
        {
            if (session.GetTimelineProperties() is { } timeline)
            {
                var duration = timeline.EndTime - timeline.StartTime;
                info = info with
                {
                    Position = timeline.Position,
                    Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                    HasTimeline = true,
                    LastUpdated = timeline.LastUpdatedTime,
                };
            }
        }
        catch (Exception)
        {
            // No timeline: the bar stays empty, which is honest.
        }

        return info;
    }

    private static MediaPlaybackStatus ToStatus(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackStatus.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackStatus.Opened,
            _ => MediaPlaybackStatus.Closed,
        };

    /// <summary>
    /// Fetches title/artist/album/cover, but only when the track changed — these are the costly calls
    /// and re-running them 3× a second would throw away the point of polling.
    /// </summary>
    private async Task LoadTrackIfChangedAsync(
        GlobalSystemMediaTransportControlsSession session,
        MediaSessionInfo info)
    {
        GlobalSystemMediaTransportControlsSessionMediaProperties? props;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception)
        {
            return;
        }

        if (props is null) return;

        // The cheap identity check happens *before* the decode, and deliberately does not include the
        // position: a player ticking its timeline must not re-decode the same cover.
        var identity = $"{info.SourceId}\u0001{props.Title}\u0001{props.Artist}";
        if (identity == _mediaKey) return;

        _mediaKey = identity;
        _title = props.Title ?? string.Empty;
        _artist = props.Artist ?? string.Empty;
        _album = props.AlbumTitle ?? string.Empty;
        _cover = await TryDecodeCoverAsync(props.Thumbnail);
    }

    /// <summary>
    /// Decodes the thumbnail to a frozen bitmap. Done off the UI thread because JPEG decode is CPU
    /// work and the point of the polling design is that the UI thread never does any.
    /// </summary>
    private static async Task<BitmapImage?> TryDecodeCoverAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;

        try
        {
            byte[] bytes;
            using (var stream = await thumbnail.OpenReadAsync())
            {
                if (stream.Size == 0) return null;

                bytes = new byte[stream.Size];
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            return await Task.Run(() =>
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(bytes);
                // OnLoad detaches the bitmap from the stream, which is what lets the MemoryStream be
                // collected and the image be frozen for cross-thread use.
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            });
        }
        catch (Exception)
        {
            // No cover is a normal state (some sources never provide one); the card shows a placeholder.
            return null;
        }
    }

    private void Publish(MediaSnapshot snapshot)
    {
        lock (_snapshotGate) _snapshot = snapshot;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager = null;
        _cover = null;
        _oneRefreshAtATime.Dispose();
    }
}
