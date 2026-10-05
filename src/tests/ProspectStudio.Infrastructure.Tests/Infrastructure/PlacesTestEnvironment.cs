using ProspectStudio.Core.Candidates;
using ProspectStudio.Infrastructure.Overture;
using ProspectStudio.Infrastructure.Reference;
using ProspectStudio.Tests.Shared;

namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// A data folder that looks as if <c>prepare_data</c> had finished for Texas: the committed county
/// geometry under <c>refdata\</c> and the committed places Parquet under
/// <c>overture\&lt;release&gt;\</c>. Nothing here touches the network.
/// </summary>
internal sealed class PlacesTestEnvironment : IDisposable
{
    private readonly TempDirectory _directory = new();

    public PlacesTestEnvironment()
    {
        ReferenceDataFixture.Install(_directory.Path);
        PlacesDataFixture.Install(_directory.Path);

        Reference = new ReferenceDataFiles(ReferenceDataFixture.Directory(_directory.Path));
        Overture = new OvertureDataFiles(PlacesDataFixture.Directory(_directory.Path), PlacesDataFixture.Release);
    }

    public string Data => _directory.Path;

    public ReferenceDataFiles Reference { get; }

    public OvertureDataFiles Overture { get; }

    /// <summary>The production source, reading the committed fixtures.</summary>
    public IPlacesSource Source => new DuckDbPlacesSource(Overture, Reference);

    /// <summary>The Texas extract this environment installed.</summary>
    public string ExtractPath => PlacesDataFixture.PlacesParquet(_directory.Path);

    /// <summary>Removes the state's extract, for the <c>NOT_READY</c> tests.</summary>
    public void DeleteExtract() => File.Delete(ExtractPath);

    /// <summary>
    /// Rewrites the extract without the rows whose <c>taxonomy.primary</c> is
    /// <paramref name="category"/>, as a <c>prepare_data --force</c> against a changed release would.
    /// The file's length and last-write time both change, which is what a cache keyed on the extract's
    /// identity has to notice.
    /// </summary>
    public void ReplaceExtractWithout(string category)
    {
        var rebuilt = ExtractPath + ".rebuilt";

        using (var db = DuckDbSpatial.Open())
        {
            DuckDbSpatial.Execute(
                db,
                $"""
                 COPY (SELECT * FROM read_parquet('{DuckDbSpatial.PathLiteral(ExtractPath)}')
                       WHERE taxonomy.primary <> '{category.Replace("'", "''", StringComparison.Ordinal)}')
                 TO '{DuckDbSpatial.PathLiteral(rebuilt)}' (FORMAT PARQUET, COMPRESSION ZSTD)
                 """);
        }

        File.Move(rebuilt, ExtractPath, overwrite: true);
    }

    public void Dispose() => _directory.Dispose();
}
