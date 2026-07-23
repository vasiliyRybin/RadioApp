using RadioApp.Data;
using RadioApp.Models;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;

namespace RadioApp.Services
{
    public class RadioDatabaseService
    {
        /// <summary>
        /// Row shape for the raw StationTotalPlayTime read. Not an EF entity — only used
        /// to materialize the SqlQuery result.
        /// </summary>
        private class StationPlayTimeRow
        {
            public int MediaItemId { get; set; }
            public long TotalSeconds { get; set; }
        }
        private void EnsureDatabaseAndTables()
        {
            string connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = RadioDbContext.DatabasePath,
                ForeignKeys = true
            }.ToString();

            using (var connection = new SQLiteConnection(connectionString))
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                            CREATE TABLE IF NOT EXISTS MediaItems
                            (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                Title TEXT NOT NULL,
                                Description TEXT,
                                SourceType INTEGER NOT NULL,
                                StreamUrl TEXT NOT NULL,
                                WebsiteUrl TEXT,
                                Genre TEXT,
                                SortOrder INTEGER NOT NULL DEFAULT 0,
                                PlayCount INTEGER NOT NULL DEFAULT 0,
                                IsEnabled INTEGER NOT NULL DEFAULT 1
                            );

                            CREATE TABLE IF NOT EXISTS PlayHistoryItems
                            (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                MediaItemId INTEGER NOT NULL,
                                EventTime DATETIME NOT NULL,
                                EventType TEXT NOT NULL DEFAULT 'Started',
                                Comment TEXT,
                                FOREIGN KEY (MediaItemId) REFERENCES MediaItems(Id) ON DELETE CASCADE
                            );
                        ";

                    command.ExecuteNonQuery();
                }

                EnsureMediaItemsColumnExists(
                    connection,
                    "PlayCount",
                    "INTEGER NOT NULL DEFAULT 0"
                );

                EnsurePlayHistoryItemsMigrated(connection);

                DropObsoletePlaybackEventTypesTableIfExists(connection);

                EnsurePlaySessionViewExists(connection);

                EnsureStationTotalPlayTimeViewExists(connection);
            }
        }

        private void EnsureMediaItemsColumnExists(
                            SQLiteConnection connection,
                            string columnName,
                            string columnDefinition)
        {
            using (var checkCommand = connection.CreateCommand())
            {
                checkCommand.CommandText = "PRAGMA table_info(MediaItems);";

                using (SQLiteDataReader reader = checkCommand.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string existingColumnName = reader["name"].ToString();

                        if (existingColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                }
            }

            using (var alterCommand = connection.CreateCommand())
            {
                alterCommand.CommandText =
                    "ALTER TABLE MediaItems ADD COLUMN " + columnName + " " + columnDefinition + ";";

                alterCommand.ExecuteNonQuery();
            }
        }

        private void EnsurePlaySessionViewExists(SQLiteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                        DROP VIEW IF EXISTS StationPlaySessions;

                        CREATE VIEW StationPlaySessions AS
                        WITH OrderedEvents AS
                        (
                            SELECT
                                Id,
                                MediaItemId,
                                EventTime,
                                EventType,

                                LEAD(EventTime) OVER
                                (
                                    PARTITION BY MediaItemId
                                    ORDER BY EventTime, Id
                                ) AS StopTime,

                                LEAD(EventType) OVER
                                (
                                    PARTITION BY MediaItemId
                                    ORDER BY EventTime, Id
                                ) AS NextEventType

                            FROM PlayHistoryItems
                            WHERE EventType IN ('Started', 'Stopped')
                        ),

                        Sessions AS
                        (
                            SELECT
                                MediaItemId,
                                EventTime AS StartTime,
                                StopTime,

                                CAST
                                (
                                    ROUND((julianday(StopTime) - julianday(EventTime)) * 86400)
                                    AS INTEGER
                                ) AS DurationSeconds

                            FROM OrderedEvents
                            WHERE EventType = 'Started'
                              AND NextEventType = 'Stopped'
                              AND StopTime IS NOT NULL
                        )

                        SELECT
                            s.MediaItemId,
                            m.Title AS StationTitle,
                            s.StartTime,
                            s.StopTime,
                            s.DurationSeconds,

                            printf
                            (
                                '%02d:%02d:%02d',
                                s.DurationSeconds / 3600,
                                (s.DurationSeconds % 3600) / 60,
                                s.DurationSeconds % 60
                            ) AS DurationFormatted

                        FROM Sessions s
                        LEFT JOIN MediaItems m ON m.Id = s.MediaItemId;
                    ";

                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Creates (or refreshes) the StationTotalPlayTime view, which aggregates the
        /// per-session StationPlaySessions view into one row per station with the grand
        /// total of played seconds. Recreated on every startup so it always matches the
        /// current definition. Depends on StationPlaySessions, so it must run after
        /// EnsurePlaySessionViewExists.
        /// </summary>
        private void EnsureStationTotalPlayTimeViewExists(SQLiteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                        DROP VIEW IF EXISTS StationTotalPlayTime;

                        CREATE VIEW StationTotalPlayTime AS
                        SELECT
                            MediaItemId,
                            StationTitle,
                            SUM(DurationSeconds) AS TotalSeconds
                        FROM StationPlaySessions
                        GROUP BY MediaItemId;
                    ";

                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migrates the PlayHistoryItems schema across its three historical shapes:
        ///   1. WhenPlayed (DATETIME) only
        ///   2. EventTime (DATETIME) + EventType (INTEGER: 0/1/2)
        ///   3. EventTime (DATETIME) + EventType (TEXT: "Started"/"Stopped"/"Error"/"TrackChanged")
        /// Safe to call on every startup; detects what's already in place and only
        /// does the missing work.
        /// </summary>
        private void EnsurePlayHistoryItemsMigrated(SQLiteConnection connection)
        {
            bool hasEventTime = ColumnExists(connection, "PlayHistoryItems", "EventTime");
            bool hasWhenPlayed = ColumnExists(connection, "PlayHistoryItems", "WhenPlayed");
            bool hasEventType = ColumnExists(connection, "PlayHistoryItems", "EventType");

            if (hasWhenPlayed && !hasEventTime)
            {
                using (var renameCommand = connection.CreateCommand())
                {
                    // Supported on SQLite 3.25.0+ (bundled System.Data.SQLite is recent enough).
                    renameCommand.CommandText =
                        "ALTER TABLE PlayHistoryItems RENAME COLUMN WhenPlayed TO EventTime;";

                    renameCommand.ExecuteNonQuery();
                }
            }

            if (!hasEventType)
            {
                using (var addColumnCommand = connection.CreateCommand())
                {
                    addColumnCommand.CommandText =
                        "ALTER TABLE PlayHistoryItems ADD COLUMN EventType TEXT NOT NULL DEFAULT 'Started';";

                    addColumnCommand.ExecuteNonQuery();
                }
            }

            // Backfill rows from the brief intermediate schema where EventType was
            // stored as an integer (0/1/2). SQLite's INTEGER column affinity still
            // accepts TEXT, so this is a plain data fix, not a schema change.
            using (var backfillCommand = connection.CreateCommand())
            {
                backfillCommand.CommandText = @"
                    UPDATE PlayHistoryItems
                    SET EventType = CASE EventType
                        WHEN '0' THEN 'Started'
                        WHEN '1' THEN 'Stopped'
                        WHEN '2' THEN 'TrackChanged'
                        ELSE EventType
                    END
                    WHERE EventType IN ('0', '1', '2');
                ";

                backfillCommand.ExecuteNonQuery();
            }

            bool hasComment = ColumnExists(connection, "PlayHistoryItems", "Comment");
            bool hasTrackName = ColumnExists(connection, "PlayHistoryItems", "TrackName");

            if (hasTrackName && !hasComment)
            {
                using (var renameCommand = connection.CreateCommand())
                {
                    renameCommand.CommandText =
                        "ALTER TABLE PlayHistoryItems RENAME COLUMN TrackName TO Comment;";

                    renameCommand.ExecuteNonQuery();
                }
            }
            else if (!hasComment)
            {
                using (var addColumnCommand = connection.CreateCommand())
                {
                    addColumnCommand.CommandText =
                        "ALTER TABLE PlayHistoryItems ADD COLUMN Comment TEXT;";

                    addColumnCommand.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Removes the PlaybackEventTypes lookup table from an earlier iteration
        /// of this feature. It's no longer needed now that EventType is stored as
        /// self-describing text directly in PlayHistoryItems.
        /// </summary>
        private void DropObsoletePlaybackEventTypesTableIfExists(SQLiteConnection connection)
        {
            using (var dropCommand = connection.CreateCommand())
            {
                dropCommand.CommandText = "DROP TABLE IF EXISTS PlaybackEventTypes;";
                dropCommand.ExecuteNonQuery();
            }
        }

        private bool ColumnExists(SQLiteConnection connection, string tableName, string columnName)
        {
            using (var checkCommand = connection.CreateCommand())
            {
                checkCommand.CommandText = "PRAGMA table_info(" + tableName + ");";

                using (SQLiteDataReader reader = checkCommand.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string existingColumnName = reader["name"].ToString();

                        if (existingColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public async Task<List<MediaItem>> GetEnabledMediaItems()
        {
            using (var db = new RadioDbContext())
            {
                return await db.MediaItems
                                .Where(x => x.IsEnabled)
                                .OrderBy(x => x.SortOrder)
                                .ThenBy(x => x.Title)
                                .ToListAsync();
            }
        }

        /// <summary>
        /// Returns total played seconds per station, read from the StationTotalPlayTime
        /// view (which only counts finished Started->Stopped sessions). Stations with no
        /// finished sessions are simply absent from the dictionary; callers treat a
        /// missing id as 0.
        /// </summary>
        public async Task<Dictionary<int, long>> GetStationTotalPlaySecondsAsync()
        {
            using (var db = new RadioDbContext())
            {
                var rows = await db.Database
                    .SqlQuery<StationPlayTimeRow>(
                        "SELECT MediaItemId, TotalSeconds FROM StationTotalPlayTime")
                    .ToListAsync();

                return rows.ToDictionary(r => r.MediaItemId, r => r.TotalSeconds);
            }
        }

        /// <summary>
        /// Reads the total played seconds for a single station on demand. Used by the
        /// station tooltip so the figure is always current at hover time. Returns 0 when
        /// the station has no finished sessions yet.
        /// </summary>
        public long GetStationTotalPlaySeconds(int mediaItemId)
        {
            using (var db = new RadioDbContext())
            {
                long? total = db.Database
                    .SqlQuery<long?>(
                        "SELECT SUM(DurationSeconds) FROM StationPlaySessions WHERE MediaItemId = @p0",
                        mediaItemId)
                    .FirstOrDefault();

                return total ?? 0L;
            }
        }

        /// <summary>
        /// Records a playback event (started, stopped, error, or track changed) in
        /// the play history table. The event type is stored as plain text.
        /// </summary>
        public async Task LogPlaybackEventAsync(
            int mediaItemId,
            PlaybackEventType eventType,
            string comment)
        {
            using (var db = new RadioDbContext())
            {
                db.PlayHistoryItems.Add(new PlayHistoryItem
                {
                    MediaItemId = mediaItemId,
                    EventType = eventType.ToString(),
                    Comment = comment ?? string.Empty,
                    EventTime = DateTime.Now
                });

                await db.SaveChangesAsync();
            }
        }

        public List<PlayHistoryItem> GetRecentHistory(int count)
        {
            using (var db = new RadioDbContext())
            {
                return db.PlayHistoryItems
                         .Include(x => x.MediaItem)
                         .OrderByDescending(x => x.EventTime)
                         .Take(count)
                         .ToList();
            }
        }

        public Task<List<MediaItem>> InitializeDatabaseAndGetEnabledMediaItemsAsync()
        {
            return Task.Run(() =>
            {
                EnsureDatabaseAndTables();

                using (var db = new RadioDbContext())
                {
                    return db.MediaItems
                             .Where(x => x.IsEnabled)
                             .OrderBy(x => x.SortOrder)
                             .ThenBy(x => x.Title)
                             .ToList();
                }
            });
        }

        public void AddMediaItem(MediaItem item)
        {
            using (var db = new RadioDbContext())
            {
                db.MediaItems.Add(item);
                db.SaveChanges();
            }
        }


        public void UpdateRadioStation(MediaItem updatedItem)
        {
            using (var db = new RadioDbContext())
            {
                var item = db.MediaItems.FirstOrDefault(x => x.Id == updatedItem.Id);

                if (item == null)
                {
                    throw new System.InvalidOperationException("Station was not found in database.");
                }

                item.Title = updatedItem.Title ?? string.Empty;
                item.Description = updatedItem.Description ?? string.Empty;
                item.StreamUrl = updatedItem.StreamUrl ?? string.Empty;

                db.SaveChanges();
            }
        }

        private string NormalizeStreamUrl(string streamUrl)
        {
            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                return string.Empty;
            }

            return streamUrl
                .Trim()
                .TrimEnd('/')
                .ToLowerInvariant();
        }

        public MediaItem AddOrUpdateRadioStation(
            string title,
            string description,
            string pageUrl,
            string streamUrl,
            string genre)
        {
            title = Truncate(title, 200);
            description = Truncate(description, 1000);
            pageUrl = Truncate(pageUrl, 1000);
            streamUrl = Truncate(streamUrl, 1000);
            genre = Truncate(genre, 100);

            using (var db = new RadioDbContext())
            {
                string normalizedNewStreamUrl = NormalizeStreamUrl(streamUrl);

                var existingItems = db.MediaItems
                    .Where(x => x.SourceType == MediaSourceType.Radio)
                    .ToList();

                var existingItem = existingItems.FirstOrDefault(x =>
                    NormalizeStreamUrl(x.StreamUrl) == normalizedNewStreamUrl
                );

                if (existingItem != null)
                {
                    existingItem.Title = string.IsNullOrWhiteSpace(title)
                        ? existingItem.Title
                        : title;

                    existingItem.Description = description ?? string.Empty;
                    existingItem.WebsiteUrl = pageUrl ?? string.Empty;
                    existingItem.StreamUrl = streamUrl ?? string.Empty;
                    existingItem.Genre = genre ?? string.Empty;
                    existingItem.IsEnabled = true;

                    db.SaveChanges();

                    return existingItem;
                }

                int nextSortOrder = 1;

                if (db.MediaItems.Any())
                {
                    nextSortOrder = db.MediaItems.Max(x => x.SortOrder) + 1;
                }

                var newItem = new MediaItem
                {
                    Title = title ?? string.Empty,
                    Description = description ?? string.Empty,
                    SourceType = MediaSourceType.Radio,
                    WebsiteUrl = pageUrl ?? string.Empty,
                    StreamUrl = streamUrl ?? string.Empty,
                    Genre = genre ?? string.Empty,
                    SortOrder = nextSortOrder,
                    IsEnabled = true
                };

                db.MediaItems.Add(newItem);
                db.SaveChanges();

                return newItem;
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            value = value.Trim();

            if (value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }

        public async Task UpdateSortOrderAsync(List<MediaItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            using (var db = new RadioDbContext())
            {
                foreach (MediaItem item in items)
                {
                    MediaItem dbItem = db.MediaItems.FirstOrDefault(x => x.Id == item.Id);

                    if (dbItem == null)
                    {
                        continue;
                    }

                    dbItem.SortOrder = item.SortOrder;
                }

                await db.SaveChangesAsync();
            }
        }

        public async Task IncrementPlayCountAsync(int mediaItemId)
        {
            using (var db = new RadioDbContext())
            {
                await db.Database.ExecuteSqlCommandAsync(
                    "UPDATE MediaItems SET PlayCount = PlayCount + 1 WHERE Id = @p0",
                    mediaItemId
                );
            }
        }

        public void DeleteMediaItem(int mediaItemId)
        {
            using (var db = new RadioDbContext())
            {
                var item = db.MediaItems.FirstOrDefault(x => x.Id == mediaItemId);

                if (item == null)
                {
                    return;
                }

                db.MediaItems.Remove(item);
                db.SaveChanges();
            }
        }
    }
}