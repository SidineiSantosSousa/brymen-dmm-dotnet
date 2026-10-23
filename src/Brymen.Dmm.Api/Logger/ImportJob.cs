using System.Text.Json.Serialization;
using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Api.Logger;

public enum ImportJobState
{
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("canceled")] Canceled,
}

/// <summary>Fotografia de um job de importação.</summary>
internal sealed record ImportJobSnapshot(
    string Id,
    ImportJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    LoggerReadProgress Progress,
    ApiError? Error,
    LoggerMemory? Memory,
    string? ImagePath,
    IReadOnlyList<string> Warnings);

/// <summary>Job de importação da memória do datalogger ("Import data from DMM").</summary>
internal sealed class ImportJob(string id, DateTimeOffset createdAt)
{
    private readonly Lock _lock = new();
    private ImportJobState _state = ImportJobState.Running;
    private DateTimeOffset? _completedAt;
    private LoggerReadProgress _progress;
    private ApiError? _error;
    private LoggerMemory? _memory;
    private string? _imagePath;
    private IReadOnlyList<string> _warnings = [];

    public string Id { get; } = id;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public CancellationTokenSource Cancellation { get; } = new();

    public Task Completion { get; set; } = Task.CompletedTask;

    public bool IsRunning
    {
        get { lock (_lock) return _state == ImportJobState.Running; }
    }

    public ImportJobSnapshot GetSnapshot()
    {
        lock (_lock)
            return new ImportJobSnapshot(Id, _state, CreatedAt, _completedAt, _progress, _error, _memory, _imagePath, _warnings);
    }

    public void ReportProgress(LoggerReadProgress progress)
    {
        lock (_lock)
            _progress = progress;
    }

    public void Complete(LoggerMemory memory, string imagePath, LoggerReadProgress progress, IReadOnlyList<string> warnings, DateTimeOffset at)
    {
        lock (_lock)
        {
            _state = ImportJobState.Completed;
            _memory = memory;
            _imagePath = imagePath;
            _progress = progress;
            _warnings = [.. warnings, .. memory.Warnings];
            _completedAt = at;
        }
    }

    public void Fail(ApiError error, DateTimeOffset at, string? imagePath = null)
    {
        lock (_lock)
        {
            _state = ImportJobState.Failed;
            _error = error;
            _imagePath ??= imagePath;
            _completedAt = at;
        }
    }

    public void MarkCanceled(DateTimeOffset at)
    {
        lock (_lock)
        {
            _state = ImportJobState.Canceled;
            _completedAt = at;
        }
    }

    public void SetImagePath(string path)
    {
        lock (_lock)
            _imagePath = path;
    }
}
