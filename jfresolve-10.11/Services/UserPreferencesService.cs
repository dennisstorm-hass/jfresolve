using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;

namespace Jfresolve.Services;

/// <summary>
/// Stores and retrieves per-user playback preferences (e.g. prefer HDR over Dolby Vision).
/// </summary>
public class UserPreferencesService
{
    private const string PrefsFileName = "Jfresolve_user_prefs.json";
    private readonly string _prefsPath;
    private readonly ReaderWriterLockSlim _lock = new();
    private Dictionary<Guid, Configuration.UserPlaybackPrefs>? _cache;

    public UserPreferencesService(IApplicationPaths applicationPaths)
    {
        _prefsPath = Path.Combine(applicationPaths.PluginConfigurationsPath, PrefsFileName);
    }

    public Configuration.UserPlaybackPrefs Get(Guid userId)
    {
        var all = Load();
        return all.TryGetValue(userId, out var prefs) ? prefs : new Configuration.UserPlaybackPrefs();
    }

    public void Set(Guid userId, Configuration.UserPlaybackPrefs prefs)
    {
        _lock.EnterWriteLock();
        try
        {
            var all = Load();
            all[userId] = prefs;
            Save(all);
            _cache = all;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private Dictionary<Guid, Configuration.UserPlaybackPrefs> Load()
    {
        _lock.EnterReadLock();
        try
        {
            if (_cache != null)
                return new Dictionary<Guid, Configuration.UserPlaybackPrefs>(_cache);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        _lock.EnterWriteLock();
        try
        {
            if (_cache != null)
                return new Dictionary<Guid, Configuration.UserPlaybackPrefs>(_cache);

            if (!File.Exists(_prefsPath))
            {
                _cache = new Dictionary<Guid, Configuration.UserPlaybackPrefs>();
                return _cache;
            }

            try
            {
                var json = File.ReadAllText(_prefsPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, Configuration.UserPlaybackPrefs>>(json);
                var result = new Dictionary<Guid, Configuration.UserPlaybackPrefs>();
                if (dict != null)
                {
                    foreach (var kv in dict)
                    {
                        if (Guid.TryParse(kv.Key, out var guid))
                            result[guid] = kv.Value ?? new Configuration.UserPlaybackPrefs();
                    }
                }
                _cache = result;
                return new Dictionary<Guid, Configuration.UserPlaybackPrefs>(result);
            }
            catch
            {
                _cache = new Dictionary<Guid, Configuration.UserPlaybackPrefs>();
                return _cache;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void Save(Dictionary<Guid, Configuration.UserPlaybackPrefs> all)
    {
        var dir = Path.GetDirectoryName(_prefsPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var dict = new Dictionary<string, Configuration.UserPlaybackPrefs>();
        foreach (var kv in all)
            dict[kv.Key.ToString("N")] = kv.Value;

        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_prefsPath, json);
    }
}
