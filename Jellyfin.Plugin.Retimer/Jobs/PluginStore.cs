using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Retimer.Jobs;

/// <summary>
/// Keeps <see cref="StoreData"/> in memory and writes it to a file after changes.
/// </summary>
internal sealed class PluginStore : IDisposable
{
    internal const int MaxFinishedJobs = 300;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    private readonly Lock _lock = new();
    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private StoreData _data;
    private bool _dirty;

    public PluginStore(string directory, ILogger logger)
    {
        _directory = directory;
        _logger = logger;
        _data = Load();
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Gets the folder that holds the downloads the plugin keeps.</summary>
    public string DownloadsDirectory => Path.Combine(_directory, "downloads");

    private string FilePath => Path.Combine(_directory, "state.json");

    public static string ItemKey(Guid itemId, string language) => itemId.ToString("N") + ":" + language;

    public static string ShowKey(Guid showId) => showId.ToString("N");

    public static string Today() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public T Read<T>(Func<StoreData, T> read)
    {
        lock (_lock)
        {
            return read(_data);
        }
    }

    public void Update(Action<StoreData> change)
    {
        lock (_lock)
        {
            change(_data);
            _dirty = true;
        }

        // Several changes in a row become one write.
        _timer.Change(750, Timeout.Infinite);
    }

    /// <summary>A copy of what is known about a show. Changes go through <see cref="UpdateShow"/>.</summary>
    public ShowRecord? Show(Guid showId) => Read(data => data.Shows.GetValueOrDefault(ShowKey(showId))?.Snapshot());

    /// <summary>A copy of what the plugin did for a video. Changes go through <see cref="UpdateItem"/>.</summary>
    public ItemRecord? Item(Guid itemId, string language) => Read(data => data.Items.GetValueOrDefault(ItemKey(itemId, language))?.Snapshot());

    public void UpdateShow(Guid showId, Action<ShowRecord> change) => Update(data =>
    {
        var key = ShowKey(showId);
        if (!data.Shows.TryGetValue(key, out var record))
        {
            record = new ShowRecord();
            data.Shows[key] = record;
        }

        change(record);
    });

    public void UpdateItem(Guid itemId, string language, Action<ItemRecord> change) => Update(data =>
    {
        var key = ItemKey(itemId, language);
        if (!data.Items.TryGetValue(key, out var record))
        {
            record = new ItemRecord { Language = language };
            data.Items[key] = record;
        }

        change(record);
    });

    public void Flush()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            json = JsonSerializer.Serialize(_data, _json);
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The plugin's state could not be written to {Path}", FilePath);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }

    private StoreData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath), _json) ?? new StoreData();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "The plugin's state at {Path} could not be read; starting empty", FilePath);
        }

        return new StoreData();
    }
}
