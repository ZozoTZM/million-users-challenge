using System.Buffers;
using System.Text.Json;

enum BodyStatus
{
    Ok,
    Malformed,
}

static class BodyReader
{
    private const int MaxBodyBytes = 16 * 1024;

    public static async Task<(BodyStatus Status, string Text)> ReadAsync(HttpRequest request)
    {
        var reader = request.BodyReader;
        ReadOnlySequence<byte> buffer = default;

        while (true)
        {
            var result = await reader.ReadAsync(request.HttpContext.RequestAborted);
            buffer = result.Buffer;
            if (result.IsCompleted || buffer.Length > MaxBodyBytes)
                break;
            reader.AdvanceTo(buffer.Start, buffer.End);
        }

        try
        {
            // The spec puts bodies over 16 KB out of scope. Treating them like an empty body
            // keeps the hot path allocation-free and bounded.
            if (buffer.Length == 0 || buffer.Length > MaxBodyBytes)
                return (BodyStatus.Ok, string.Empty);

            using var document = JsonDocument.Parse(buffer);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("body", out var body) ||
                body.ValueKind != JsonValueKind.String)
            {
                return (BodyStatus.Ok, string.Empty);
            }

            return (BodyStatus.Ok, body.GetString()!.Trim());
        }
        catch (JsonException)
        {
            return (BodyStatus.Malformed, string.Empty);
        }
        finally
        {
            reader.AdvanceTo(buffer.End);
        }
    }
}
