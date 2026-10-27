using System.Text.Json.Serialization;

// Wire DTOs. System.Text.Json's source generator creates the metadata; no reflection at runtime.
// JsonPropertyOrder pins the key order required by SPEC.md.
sealed record PostDto(
    [property: JsonPropertyOrder(0)] long Id,
    [property: JsonPropertyOrder(1)] string Body,
    [property: JsonPropertyOrder(2)] string CreatedAt,
    [property: JsonPropertyOrder(3)] string Author,
    [property: JsonPropertyOrder(4)] int LikeCount);

sealed record FeedResponse(
    [property: JsonPropertyOrder(0)] List<PostDto> Posts);

sealed record PostResponse(
    [property: JsonPropertyOrder(0)] PostDto Post);

sealed record LikeResponse(
    [property: JsonPropertyOrder(0)] bool Liked,
    [property: JsonPropertyOrder(1)] bool AlreadyLiked,
    [property: JsonPropertyOrder(2)] long PostId);

sealed record HealthResponse(
    [property: JsonPropertyOrder(0)] string Status,
    [property: JsonPropertyOrder(1)] string Db,
    [property: JsonPropertyOrder(2)] long UptimeS);

sealed record DegradedResponse(
    [property: JsonPropertyOrder(0)] string Status,
    [property: JsonPropertyOrder(1)] string Db,
    [property: JsonPropertyOrder(2)] string Error);

sealed record ErrorResponse(
    [property: JsonPropertyOrder(0)] string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FeedResponse))]
[JsonSerializable(typeof(PostResponse))]
[JsonSerializable(typeof(LikeResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(DegradedResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal partial class FeedJsonContext : JsonSerializerContext
{
}
