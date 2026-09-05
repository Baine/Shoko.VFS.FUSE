// SignalR connection manager for the Shoko Server aggregate hub, modeled on
// Shokofin's Shokofin/SignalR/SignalRConnectionManager.cs.
//
// Hub: /signalr/aggregate?feeds=<csv>. Feed group names are derived server-side from the
// emitter type (Shoko.Server/API/SignalR/Aggregate/BaseEventEmitter.cs: type name minus
// "EventEmitter", lowercased), so the managed folder feed is "managedfolder". Event names
// are "<feed>:<subject>".
//
// Payloads are Newtonsoft-serialized PascalCase by the server; the client protocol is
// configured with case-sensitive, naming-policy-free System.Text.Json options so the
// PascalCase wire properties match the DTOs below 1:1.

using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Shoko.VFS.FUSE.Host.SignalR;

#region Payload DTOs (Shoko.Server/API/SignalR/Models, PascalCase wire shape)

/// <summary><c>file:deleted</c> — VideoFileEventSignalRModel.</summary>
public class FileEventPayload
{
    public int FileID { get; set; }

    public int FileLocationID { get; set; }

    public int ManagedFolderID { get; set; }

    public string RelativePath { get; set; } = "";
}

/// <summary><c>file:detected</c> — VideoFileDetectedEventSignalRModel.</summary>
public sealed class FileDetectedPayload
{
    public string RelativePath { get; set; } = "";

    public int ManagedFolderID { get; set; }
}

/// <summary><c>file:hashed</c> — VideoFileHashedEventSignalrRModel (extends the base file event).</summary>
public sealed class FileHashedPayload : FileEventPayload
{
    public bool UsedExistingHashes { get; set; }

    public bool IsNewVideo { get; set; }

    public bool IsNewFile { get; set; }
}

/// <summary><c>file:relocated</c> — VideoFileRelocatedEventSignalRModel (extends the base file event).</summary>
public sealed class FileRelocatedPayload : FileEventPayload
{
    public bool Moved { get; set; }

    public bool Renamed { get; set; }

    public string PreviousRelativePath { get; set; } = "";

    public int PreviousManagedFolderID { get; set; }
}

/// <summary><c>managedfolder:added|updated|removed</c> — ManagedFolderChangedSignalRModel.</summary>
public sealed class ManagedFolderChangedPayload
{
    public int FolderID { get; set; }
}

/// <summary><c>release:saved</c> — VideoReleaseSavedSignalRModel (release details refetched over REST).</summary>
public sealed class ReleaseSavedPayload
{
    public int FileID { get; set; }
}

/// <summary><c>release:removed</c> — ReleaseDeletedSignalRModel (release details omitted).</summary>
public sealed class ReleaseRemovedPayload
{
    /// <summary>Video id, if it was available when the release was deleted.</summary>
    public int? FileID { get; set; }
}

#endregion

#region Event args

public sealed record FileDetectedEventArgs(int ManagedFolderId, string RelativePath);

public sealed record FileHashedEventArgs(
    int ManagedFolderId,
    string RelativePath,
    int FileId,
    int FileLocationId,
    bool UsedExistingHashes,
    bool IsNewVideo,
    bool IsNewFile);

public sealed record FileRelocatedEventArgs(
    int PreviousManagedFolderId,
    string PreviousRelativePath,
    int ManagedFolderId,
    string RelativePath,
    int FileId,
    int FileLocationId,
    bool Moved,
    bool Renamed);

public sealed record FileDeletedEventArgs(
    int ManagedFolderId,
    string RelativePath,
    int FileId,
    int FileLocationId);

public sealed record ManagedFolderChangedEventArgs(int FolderId);

public sealed record ReleaseSavedEventArgs(int FileId);

public sealed record ReleaseRemovedEventArgs(int? FileId);

#endregion

/// <summary>
/// Keeps a SignalR connection to the Shoko Server aggregate hub and re-emits file,
/// managed-folder and release events as plain C# events so the daemon can mark
/// content/topology dirty.
/// </summary>
public sealed class ShokoSignalRConnection : IAsyncDisposable
{
    private const string HubPath = "/signalr/aggregate?feeds=metadata,file,managedfolder,release";

    private readonly string _hubUrl;
    private readonly string _apiKey;
    private readonly Action<string>? _log;
    private HubConnection? _connection;

    public ShokoSignalRConnection(string baseUrl, string apiKey, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL must be specified.", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key must be specified.", nameof(apiKey));
        _hubUrl = (baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/") + HubPath.TrimStart('/');
        _apiKey = apiKey;
        _log = log;
    }

    public event EventHandler<FileDetectedEventArgs>? FileDetected;
    public event EventHandler<FileHashedEventArgs>? FileHashed;
    public event EventHandler<FileRelocatedEventArgs>? FileRelocated;
    public event EventHandler<FileDeletedEventArgs>? FileDeleted;
    public event EventHandler<ManagedFolderChangedEventArgs>? ManagedFolderAdded;
    public event EventHandler<ManagedFolderChangedEventArgs>? ManagedFolderUpdated;
    public event EventHandler<ManagedFolderChangedEventArgs>? ManagedFolderRemoved;
    public event EventHandler<ReleaseSavedEventArgs>? ReleaseSaved;
    public event EventHandler<ReleaseRemovedEventArgs>? ReleaseRemoved;
    public event EventHandler? Reconnected;

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    public bool IsConnected => _connection is { State: HubConnectionState.Connected };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
            return;

        var builder = new HubConnectionBuilder()
            .WithUrl(_hubUrl, options => options.AccessTokenProvider = () => Task.FromResult<string?>(_apiKey))
            .AddJsonProtocol(options =>
            {
                // Server emits Newtonsoft PascalCase payloads; match exactly.
                options.PayloadSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.General);
            })
            .WithAutomaticReconnect();

        var connection = builder.Build();
        connection.ServerTimeout = TimeSpan.FromMinutes(1);
        connection.Closed += exception =>
        {
            Log(exception is null ? "SignalR: gracefully disconnected." : $"SignalR: abruptly disconnected ({exception?.Message}).");
            return Task.CompletedTask;
        };
        connection.Reconnecting += _ =>
        {
            Log("SignalR: reconnecting...");
            return Task.CompletedTask;
        };
        connection.Reconnected += _ =>
        {
            Log("SignalR: reconnected.");
            Reconnected?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        };

        connection.On<FileDetectedPayload>("file:detected", payload =>
            FileDetected?.Invoke(this, new FileDetectedEventArgs(payload.ManagedFolderID, payload.RelativePath)));
        connection.On<FileHashedPayload>("file:hashed", payload =>
            FileHashed?.Invoke(this, new FileHashedEventArgs(
                payload.ManagedFolderID, payload.RelativePath, payload.FileID, payload.FileLocationID,
                payload.UsedExistingHashes, payload.IsNewVideo, payload.IsNewFile)));
        connection.On<FileRelocatedPayload>("file:relocated", payload =>
            FileRelocated?.Invoke(this, new FileRelocatedEventArgs(
                payload.PreviousManagedFolderID, payload.PreviousRelativePath,
                payload.ManagedFolderID, payload.RelativePath, payload.FileID, payload.FileLocationID,
                payload.Moved, payload.Renamed)));
        connection.On<FileEventPayload>("file:deleted", payload =>
            FileDeleted?.Invoke(this, new FileDeletedEventArgs(
                payload.ManagedFolderID, payload.RelativePath, payload.FileID, payload.FileLocationID)));

        connection.On<ManagedFolderChangedPayload>("managedfolder:added", payload =>
            ManagedFolderAdded?.Invoke(this, new ManagedFolderChangedEventArgs(payload.FolderID)));
        connection.On<ManagedFolderChangedPayload>("managedfolder:updated", payload =>
            ManagedFolderUpdated?.Invoke(this, new ManagedFolderChangedEventArgs(payload.FolderID)));
        connection.On<ManagedFolderChangedPayload>("managedfolder:removed", payload =>
            ManagedFolderRemoved?.Invoke(this, new ManagedFolderChangedEventArgs(payload.FolderID)));

        connection.On<ReleaseSavedPayload>("release:saved", payload =>
            ReleaseSaved?.Invoke(this, new ReleaseSavedEventArgs(payload.FileID)));
        connection.On<ReleaseRemovedPayload>("release:removed", payload =>
            ReleaseRemoved?.Invoke(this, new ReleaseRemovedEventArgs(payload.FileID)));

        _connection = connection;
        try
        {
            await connection.StartAsync(cancellationToken);
            Log("SignalR: connected to " + _hubUrl);
        }
        catch
        {
            _connection = null;
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
            return;

        if (connection.State != HubConnectionState.Disconnected)
            await connection.StopAsync();
        await connection.DisposeAsync();
    }

    public ValueTask DisposeAsync()
        => new(StopAsync());

    private void Log(string message) => _log?.Invoke(message);
}