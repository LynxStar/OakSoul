using System.Text.Json;
using System.Text.Json.Serialization;

namespace OakSoul.Recorder;

/// <summary>One line per delivered frame, so recordings can be joined back to video frames and keyframes.</summary>
public sealed record FrameRecord(
    long Sequence,
    double Pts,
    DateTime WallClockUtc,
    int VideoFrame,
    string? Keyframe);

public sealed class MetadataWriter : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly StreamWriter _writer;

    public MetadataWriter(string path)
    {
        _writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1 << 16);
    }

    public void Write(FrameRecord record) => _writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));

    public void Dispose() => _writer.Dispose();
}
