// Builds the Census CBP response fixtures in this folder (a .NET 10 file-based app).
//
//   dotnet run build-cbp-fixtures.cs -- --samples <dir>   # copy and trim already-recorded bodies
//   dotnet run build-cbp-fixtures.cs -- --record          # re-record from the live API (needs CENSUS_API_KEY)
//
// Every fixture is a verbatim response body from https://api.census.gov, except
// cbp_variables_2023.json, which is the real metadata document trimmed to the variables the CBP
// client reads (the untrimmed one is 540 KB and nothing in it is asserted).
//
// The key never reaches a file: --record reads CENSUS_API_KEY from the environment, appends it to the
// request URI only, and writes response bodies only. Nothing here prints or stores a URL.


using System.Text.Json;
using System.Text.Json.Nodes;

const string ApiBase = "https://api.census.gov/data";
const int CbpYear = 2023;

// Every fixture: the file written here, the recorded sample it came from, and the query that produced
// it (relative to ApiBase, with no key). The query doubles as the README's provenance row, so a
// fixture can always be reproduced by hand with curl.
Fixture[] fixtures =
[
    new("houston10_4931.json", "houston10_4931_allbands.json",
        $"/{CbpYear}/cbp?get=ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017"
        + "&for=county:015,039,071,157,167,201,291,339,407,473&in=state:48&NAICS2017=4931"),

    new("houston10_238210.json", "houston10_238210_allbands.json",
        $"/{CbpYear}/cbp?get=ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017"
        + "&for=county:015,039,071,157,167,201,291,339,407,473&in=state:48&NAICS2017=238210"),

    new("harris_4931.json", "harris_4931.json",
        $"/{CbpYear}/cbp?get=ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017&for=county:201&in=state:48&NAICS2017=4931"),

    new("harris_49311.json", "harris_49311.json",
        $"/{CbpYear}/cbp?get=ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017&for=county:201&in=state:48&NAICS2017=49311"),

    new("harris_238210.json", "harris_238210.json",
        $"/{CbpYear}/cbp?get=ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017&for=county:201&in=state:48&NAICS2017=238210"),

    new("harris_00_allbands.json", "harris_00_allbands.json",
        $"/{CbpYear}/cbp?get=ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017&for=county:201&in=state:48&NAICS2017=00"),

    new("tx_counties_4931_duplicate_column.json", "multi_county_tx_3_4931.json",
        $"/{CbpYear}/cbp?get=ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017"
        + "&for=county:157,201,473&in=state:48&NAICS2017=4931&EMPSZES=001"),

    new("tx_counties_238210_with_flags.json", "tx_all_counties_238210_allbands.json",
        $"/{CbpYear}/cbp?get=ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017,EMP,ESTAB_F,EMP_F"
        + "&for=county:*&in=state:48&NAICS2017=238210"),

    new("cross_state_error.txt", "crossstate_try.json",
        $"/{CbpYear}/cbp?get=ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017"
        + "&for=county:201,001&in=state:48,22&NAICS2017=4931",
        Expect: 400),

    new("not_found_404.html", "probe_2024.txt", "/2024/cbp/variables.json", Expect: 404, Keyed: false),

    new("cbp_variables_2023.json", "variables.json", $"/{CbpYear}/cbp/variables.json", Keyed: false, Trim: true),
];

// Run it from this folder, or pass --out and --samples.
var outputDirectory = Path.GetFullPath(".");
var samples = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "spikes", "census-cbp", "samples"));
var record = false;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--record":
            record = true;
            break;
        case "--samples" when index + 1 < args.Length:
            samples = Path.GetFullPath(args[++index]);
            break;
        case "--out" when index + 1 < args.Length:
            outputDirectory = Path.GetFullPath(args[++index]);
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[index]}'.");
            return 2;
    }
}

Directory.CreateDirectory(outputDirectory);

if (record)
{
    var key = Environment.GetEnvironmentVariable("CENSUS_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("CENSUS_API_KEY is not set, and every CBP data query needs one (302 without it).");
        return 1;
    }

    // Not following the redirect is the whole point of the recording: a followed 302 lands on a 200
    // HTML page, which is what made the key problem look like a JSON parse error.
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("ProspectStudio-tests/0.1 (+https://github.com/ajbryn)");

    foreach (var fixture in fixtures)
    {
        var uri = ApiBase + fixture.Query + (fixture.Keyed ? $"&key={key}" : string.Empty);
        using var response = await http.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();

        if ((int)response.StatusCode != fixture.Expect)
        {
            Console.Error.WriteLine(
                $"{fixture.Name}: expected HTTP {fixture.Expect}, got {(int)response.StatusCode}. "
                + "The API changed; check the decisions table in poc/implementation-plan.md before overwriting.");
            return 1;
        }

        Write(fixture, body);
    }
}
else
{
    if (!Directory.Exists(samples))
    {
        Console.Error.WriteLine(
            $"No recorded samples at '{samples}'. Pass --samples <dir>, or --record to fetch them live.");
        return 1;
    }

    foreach (var fixture in fixtures)
    {
        var source = Path.Combine(samples, fixture.Sample);
        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"{fixture.Name}: no recorded sample at '{source}'.");
            return 1;
        }

        Write(fixture, File.ReadAllText(source));
    }
}

Console.Error.WriteLine($"Wrote {fixtures.Length} fixtures to {outputDirectory}.");
return 0;

void Write(Fixture fixture, string body)
{
    var content = fixture.Trim ? TrimVariables(body) : body;
    File.WriteAllText(Path.Combine(outputDirectory, fixture.Name), content);
    Console.Error.WriteLine($"  {fixture.Name,-42} {content.Length,8} bytes");
}

// Keeps only the variables the CBP client reads, plus the geography predicates, so the document stays
// a real metadata response in miniature. EMPSZES's absent "values" list is the point of the fixture:
// it is why the bands have to be parsed from EMPSZES_LABEL instead of hard-coded.
//
// Written with JsonDocument rather than JsonNode because NAICS2017's "values.item" object repeats some
// keys ("111" twice, for one), which JsonObject refuses to load. That is the real document's shape.
static string TrimVariables(string body)
{
    // EMPSZES_LABEL, ESTAB_F and EMP_F are deliberately absent: a data query returns those columns,
    // but the metadata lists them only inside the "attributes" of the variable they annotate. One more
    // reason the client cannot learn the band set from the metadata.
    string[] keep = ["for", "in", "ucgid", "ESTAB", "EMP", "EMPSZES", "NAICS2017", "YEAR"];

    // NAICS2017 publishes all 6,694 codes as values, which is 354 KB of the 540 KB document. Keep the
    // codes the tests use: the contrast that matters is that NAICS2017 has a values list at all and
    // EMPSZES has none, which is why the bands come from the data response's labels.
    string[] keepCodes = ["00", "23821", "238210", "4931", "49311", "493110"];

    using var document = JsonDocument.Parse(body);
    if (!document.RootElement.TryGetProperty("variables", out var variables))
    {
        throw new InvalidOperationException("The metadata response has no 'variables' object.");
    }

    var missing = keep.Where(name => !variables.TryGetProperty(name, out _)).ToArray();
    if (missing.Length > 0)
    {
        throw new InvalidOperationException(
            $"The CBP metadata no longer publishes: {string.Join(", ", missing)}. "
            + "That changes what the client can read; record it in the decisions table.");
    }

    using var buffer = new MemoryStream();

    // Relaxed escaping keeps the apostrophes in "Census API FIPS 'for' clause" readable, so the fixture
    // still looks like the body it was recorded from.
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }))
    {
        writer.WriteStartObject();
        writer.WritePropertyName("variables");
        writer.WriteStartObject();

        foreach (var name in keep)
        {
            var variable = variables.GetProperty(name);
            writer.WritePropertyName(name);

            if (name != "NAICS2017")
            {
                variable.WriteTo(writer);
                continue;
            }

            writer.WriteStartObject();
            foreach (var member in variable.EnumerateObject())
            {
                if (member.Name != "values")
                {
                    member.WriteTo(writer);
                    continue;
                }

                writer.WritePropertyName("values");
                writer.WriteStartObject();
                writer.WritePropertyName("item");
                writer.WriteStartObject();

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var code in member.Value.GetProperty("item").EnumerateObject())
                {
                    if (keepCodes.Contains(code.Name) && seen.Add(code.Name))
                    {
                        code.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
}

/// <param name="Name">The fixture file written into this folder.</param>
/// <param name="Sample">The recorded body in the spike samples folder it is copied from.</param>
/// <param name="Query">The request that produced it, relative to the API base and without the key.</param>
/// <param name="Expect">The HTTP status the live API answers with.</param>
/// <param name="Keyed">False for the metadata endpoints, which answer unkeyed.</param>
/// <param name="Trim">True for the metadata document, which is cut down to the variables we read.</param>
internal readonly record struct Fixture(
    string Name,
    string Sample,
    string Query,
    int Expect = 200,
    bool Keyed = true,
    bool Trim = false);
