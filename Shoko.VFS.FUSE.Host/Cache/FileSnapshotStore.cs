using Newtonsoft.Json;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Host.Cache;

/// <summary>
/// Persists <see cref="SeriesData"/> snapshots and a clean-shutdown marker to disk
/// so the daemon can resume serving the VFS after a restart without re-aggregating
/// from the Shoko server.
///
/// File layout in the configured snapshot directory:
/// <code>
///   &lt;key&gt;.snapshot.json      the persisted snapshot (atomic write via temp+rename)
///   &lt;key&gt;.clean_shutdown    empty marker file written after a successful save
/// </code>
///
/// On startup the caller checks <see cref="WasLastShutdownClean"/>: if absent the
/// snapshot is treated as stale and the cache is invalidated (caller rebuilds).
/// </summary>
public sealed class FileSnapshotStore
{
    private readonly string _dir;
    private readonly Action<string>? _log;

    public FileSnapshotStore(string directory, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Snapshot directory must be specified.", nameof(directory));
        _dir = directory;
        _log = log;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>The configured snapshot directory (created if missing).</summary>
    public string SnapshotDirectory => _dir;

    private string SnapshotPath(string key) => Path.Combine(_dir, key + ".snapshot.json");
    private string MarkerPath(string key) => Path.Combine(_dir, key + ".clean_shutdown");

    /// <summary>
    /// Returns the cached snapshot if the file exists, or null otherwise.
    /// </summary>
    public IReadOnlyList<SeriesData>? TryLoad(string key)
    {
        var path = SnapshotPath(key);
        if (!File.Exists(path))
            return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new StreamReader(fs);
            using var json = new JsonTextReader(reader);
            return new JsonSerializer().Deserialize<List<SeriesData>>(json);
        }
        catch (Exception ex)
        {
            Log($"Snapshot load failed for {key}: {ex.Message}");
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Saves the snapshot atomically (write to temp, then rename) and writes the
    /// clean-shutdown marker. Idempotent: existing files are overwritten.
    /// </summary>
    public void Save(string key, IReadOnlyList<SeriesData> snapshot)
    {
        Directory.CreateDirectory(_dir);
        var finalPath = SnapshotPath(key);
        var tmpPath = finalPath + ".tmp";
        using (var fs = File.Create(tmpPath))
        using (var writer = new StreamWriter(fs))
        using (var json = new JsonTextWriter(writer))
        {
            JsonSerializer.CreateDefault().Serialize(json, snapshot);
            writer.Flush();
        }
        // Atomic on POSIX; on Windows File.Move with overwrite is close enough.
        if (File.Exists(finalPath))
            File.Replace(tmpPath, finalPath, destinationBackupFileName: null);
        else
            File.Move(tmpPath, finalPath);

        File.WriteAllBytes(MarkerPath(key), Array.Empty<byte>());
    }

    /// <summary>
    /// Returns true if a clean-shutdown marker exists for the key. Absence means the
    /// previous run did not finish cleanly — the snapshot may be inconsistent and
    /// should be discarded by the caller.
    /// </summary>
    public bool WasLastShutdownClean(string key) => File.Exists(MarkerPath(key));

    /// <summary>Removes both the snapshot and the clean-shutdown marker for the key.</summary>
    public void Clear(string key)
    {
        try { File.Delete(SnapshotPath(key)); } catch { }
        try { File.Delete(MarkerPath(key)); } catch { }
    }

    private void Log(string message) => _log?.Invoke(message);
}
