using System.Text.Json.Serialization;

namespace GymNotebook.Api;

// PATCH needs to distinguish a property that was omitted from one that was sent as
// JSON null. Nullable CLR properties alone cannot do that: both cases deserialize to
// null. Each init accessor therefore records whether its JSON property was present.
// The Has* flags are handler-only metadata and are excluded from the wire contract.
public sealed class UpdateWorkoutRequest
{
    private DateOnly? _date;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endedAt;
    private string? _title;
    private decimal? _bodyweightKg;
    private string? _location;
    private string? _notes;

    public DateOnly? Date
    {
        get => _date;
        init
        {
            _date = value;
            HasDate = true;
        }
    }

    [JsonIgnore]
    public bool HasDate { get; private set; }

    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        init
        {
            _startedAt = value;
            HasStartedAt = true;
        }
    }

    [JsonIgnore]
    public bool HasStartedAt { get; private set; }

    public DateTimeOffset? EndedAt
    {
        get => _endedAt;
        init
        {
            _endedAt = value;
            HasEndedAt = true;
        }
    }

    [JsonIgnore]
    public bool HasEndedAt { get; private set; }

    public string? Title
    {
        get => _title;
        init
        {
            _title = value;
            HasTitle = true;
        }
    }

    [JsonIgnore]
    public bool HasTitle { get; private set; }

    public decimal? BodyweightKg
    {
        get => _bodyweightKg;
        init
        {
            _bodyweightKg = value;
            HasBodyweightKg = true;
        }
    }

    [JsonIgnore]
    public bool HasBodyweightKg { get; private set; }

    public string? Location
    {
        get => _location;
        init
        {
            _location = value;
            HasLocation = true;
        }
    }

    [JsonIgnore]
    public bool HasLocation { get; private set; }

    public string? Notes
    {
        get => _notes;
        init
        {
            _notes = value;
            HasNotes = true;
        }
    }

    [JsonIgnore]
    public bool HasNotes { get; private set; }
}
