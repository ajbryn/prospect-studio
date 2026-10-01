using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The tools chunk C2 adds, checked against mcp-tools.md: each is advertised with a description and
/// takes the documented <c>camelCase</c> parameters, and the errors come back in the
/// <c>{code, message, hint}</c> envelope with codes from §Errors. This server has no reference data,
/// which is also what makes it the right place to check <c>NOT_READY</c>.
/// </summary>
[Collection(McpServerCollection.Name)]
public class C2ToolContractTests(McpServerFixture server)
{
    /// <summary>mcp-tools.md §Summary, chunk C2.</summary>
    public static TheoryData<string> C2Tools =>
    [
        "prepare_data",
        "get_job",
        "list_jobs",
        "cancel_job",
        "lookup_naics",
        "resolve_geography",
    ];

    [Theory]
    [MemberData(nameof(C2Tools))]
    public async Task Tools_list_advertises_the_tool_with_a_description(string name)
    {
        var tool = await ToolSchemas.FindAsync(server.Client, name);

        tool.Description.ShouldNotBeNullOrWhiteSpace(
            "Claude picks tools from their descriptions, so every tool needs one (mcp-tools.md §Conventions).");
    }

    [Fact]
    public async Task Prepare_data_takes_states_and_force()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "prepare_data");

        ToolSchemas.Properties(tool).ShouldBe(["states", "force"], ignoreOrder: true, ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task Get_job_takes_a_required_jobId()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "get_job");

        ToolSchemas.Properties(tool).ShouldBe(["jobId"], ToolSchemas.Describe(tool));
        ToolSchemas.Required(tool).ShouldBe(["jobId"], ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task List_jobs_takes_an_optional_campaignId_and_status()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "list_jobs");

        ToolSchemas.Properties(tool).ShouldBe(["campaignId", "status"], ignoreOrder: true, ToolSchemas.Describe(tool));
        ToolSchemas.Required(tool).ShouldBeEmpty(ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task Cancel_job_takes_a_required_jobId()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "cancel_job");

        ToolSchemas.Properties(tool).ShouldBe(["jobId"], ToolSchemas.Describe(tool));
        ToolSchemas.Required(tool).ShouldBe(["jobId"], ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task Lookup_naics_takes_a_required_query_and_an_optional_limit()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "lookup_naics");

        ToolSchemas.Properties(tool).ShouldBe(["query", "limit"], ignoreOrder: true, ToolSchemas.Describe(tool));
        ToolSchemas.Required(tool).ShouldBe(["query"], ToolSchemas.Describe(tool));
    }

    [Fact]
    public async Task Resolve_geography_takes_every_documented_input_form()
    {
        var tool = await ToolSchemas.FindAsync(server.Client, "resolve_geography");

        ToolSchemas.Properties(tool).ShouldBe(
            ["query", "type", "values", "center", "radiusMiles"],
            ignoreOrder: true,
            ToolSchemas.Describe(tool));

        ToolSchemas.Required(tool).ShouldBeEmpty(
            "mcp-tools.md §resolve_geography accepts any one of five input forms, so no single "
            + $"parameter is required: {ToolSchemas.Describe(tool)}");
    }

    [Fact]
    public async Task Get_job_for_an_unknown_job_is_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "get_job",
            new Dictionary<string, object?> { ["jobId"] = "job_ZZZZZZ" },
            server.StandardError);

        error.Code.ShouldBe("NOT_FOUND");
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cancel_job_for_an_unknown_job_is_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "cancel_job",
            new Dictionary<string, object?> { ["jobId"] = "job_ZZZZZZ" },
            server.StandardError);

        error.Code.ShouldBe("NOT_FOUND");
    }

    [Fact]
    public async Task Resolve_geography_without_reference_data_is_NOT_READY()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "resolve_geography",
            new Dictionary<string, object?> { ["query"] = "Houston metro" },
            server.StandardError);

        error.Code.ShouldBe(
            "NOT_READY",
            "mcp-tools.md §Errors: NOT_READY is 'setup or prerequisite missing (data...)', hinted with "
            + "'Run prepare_data for TX first.'");
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Lookup_naics_works_without_any_reference_data_download()
    {
        // naics2022.csv is committed in the Infrastructure project, so NAICS lookup is ready on a
        // first run - unlike geography, which needs prepare_data.
        var payload = await ToolCall.OkAsync(
            server.Client,
            "lookup_naics",
            new Dictionary<string, object?> { ["query"] = "warehousing", ["limit"] = 5 },
            server.StandardError);

        payload.GetProperty("results").EnumerateArray().ShouldNotBeEmpty();
    }
}
