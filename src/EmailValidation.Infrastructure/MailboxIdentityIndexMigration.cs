using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

internal static class MailboxIdentityIndexMigration
{
    // Called only after the replacement index exists. Retain legacy documents for
    // audit; never split, rename, or copy their ambiguous case-folded evidence.
    public static async Task DropLegacyAsync<T>(IMongoCollection<T> collection, string name, CancellationToken cancellationToken)
    {
        using var cursor = await collection.Indexes.ListAsync(cancellationToken).ConfigureAwait(false);
        var indexes = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!indexes.Any(index => index.GetValue("name", "").AsString == name)) return;
        try
        {
            await collection.Indexes.DropOneAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException exception) when (exception.Code == 27)
        {
            // Another starting host already completed the same migration.
        }
    }
}
