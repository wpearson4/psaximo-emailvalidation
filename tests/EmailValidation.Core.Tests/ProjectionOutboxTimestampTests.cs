using EmailValidation.Infrastructure;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EmailValidation.Core.Tests;

public sealed class ProjectionOutboxTimestampTests
{
    [Fact]
    public void OutboxTimestamps_WriteBsonDatesAndReadLegacyOffsetArrays()
    {
        var at = new DateTimeOffset(2026, 10, 10, 7, 0, 0, TimeSpan.FromHours(-5));
        var document = new MongoProjectionOutbox.ProjectionOutboxDocument
        {
            Id = "synthetic", EventType = "synthetic", SchemaVersion = "v1", PayloadJson = "{}",
            OccurredAtUtc = at, CreatedAtUtc = at, NextPublishAttemptAtUtc = at,
            LockExpiresAtUtc = at.AddMinutes(1)
        };
        var bson = document.ToBsonDocument();
        foreach (var field in new[] { "OccurredAtUtc", "CreatedAtUtc", "NextPublishAttemptAtUtc", "LockExpiresAtUtc" })
        {
            Assert.Equal(BsonType.DateTime, bson[field].BsonType);
            var value = field == "LockExpiresAtUtc" ? at.AddMinutes(1) : at;
            Assert.Equal(value.UtcDateTime, bson[field].ToUniversalTime());
            bson[field] = new BsonArray { value.Ticks, (int)value.Offset.TotalMinutes };
        }
        var restored = BsonSerializer.Deserialize<MongoProjectionOutbox.ProjectionOutboxDocument>(bson);
        Assert.Equal(at, restored.NextPublishAttemptAtUtc);
        Assert.Equal(at.AddMinutes(1), restored.LockExpiresAtUtc);
        var rewritten = restored.ToBsonDocument();
        Assert.Equal(BsonType.DateTime, rewritten["NextPublishAttemptAtUtc"].BsonType);
        Assert.Equal(BsonType.DateTime, rewritten["LockExpiresAtUtc"].BsonType);
    }
}
