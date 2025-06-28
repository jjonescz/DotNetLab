using System.Text.Json.Serialization;

namespace DotNetLab;

[JsonDerivedType(typeof(Ready), nameof(Ready))]
[JsonDerivedType(typeof(Empty), nameof(Empty))]
[JsonDerivedType(typeof(Success), nameof(Success))]
[JsonDerivedType(typeof(Failure), nameof(Failure))]
public abstract record WorkerOutputMessage
{
    public const int BroadcastId = -1;
    public const string BroadcastInputType = "Broadcast";
    public const string UnknownInputType = "Unknown";
    public const string NoInputType = "None";

    public required int Id { get; init; }

    /// <summary>
    /// Used for logging only.
    /// </summary>
    public required string InputType { get; init; }

    public bool IsBroadcast => Id == BroadcastId;

    public sealed record Ready : WorkerOutputMessage;

    public sealed record Empty : WorkerOutputMessage;

    public sealed record Success(object? Result) : WorkerOutputMessage;

    [method: JsonConstructor]
    public sealed record Failure(string Message, string FullString) : WorkerOutputMessage
    {
        public Failure(Exception ex) : this(Message: ex.Message, FullString: ex.ToString()) { }
    }
}
