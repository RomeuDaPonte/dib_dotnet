namespace Dib4t.E2E.Harness;

/// <summary>
/// One of the two apps the comparison scenarios run against (PLAN.md §11.7): the unchanged legacy app on its own
/// mongo:4.1.4, or the new app on its own mongo:9.0.8. Scenarios are written once against this interface and the
/// page objects; each app has its own page objects for its DOM (<c>Pages/Legacy</c>, <c>Pages/Blazor</c>).
/// </summary>
public interface IAppUnderTest
{
    /// <summary>"legacy" or "new", used in messages and artifact names.</summary>
    string Name { get; }

    /// <summary>Origin of the UI, e.g. <c>http://localhost:3000</c>.</summary>
    Uri UiBaseUrl { get; }

    /// <summary>Base of the API, ending with <c>/api/</c>.</summary>
    Uri ApiBaseUrl { get; }

    /// <summary>Replaces the app's database with the content of a <c>.dibbak</c> backup.</summary>
    Task RestoreAsync(string backupPath, CancellationToken cancellationToken);

    /// <summary>Every document of the app's database, for the field-by-field and BSON-type comparison.</summary>
    Task<DbSnapshot> SnapshotAsync(CancellationToken cancellationToken);
}
