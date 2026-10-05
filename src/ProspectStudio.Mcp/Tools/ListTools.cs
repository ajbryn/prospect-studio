using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tools;

/// <summary>
/// <c>import_list</c> and <c>list_dealers</c> from mcp-tools.md §Lists. Every rule lives in
/// <see cref="ListImportService"/> and <see cref="IDealerStore"/>; this class maps arguments in and
/// results or error codes out.
/// </summary>
[McpServerToolType]
public sealed class ListTools(ListImportService lists, IDealerStore dealers)
{
    [McpServerTool(Name = "import_list")]
    [Description("Imports one of the business lists from a CSV or XLSX file: dealers and their branches, dealer territories (ZIP and county rules), or a suppression list of companies never to mail. Returns how many rows were created, how many replaced an existing row, and the rows it could not use with their row numbers. Re-importing the same file changes nothing, so an edited list can be imported again safely; pass replace to let a suppression file drop entries as well as add them.")]
    public async ValueTask<CallToolResult> ImportListAsync(
        [Description("Which list to import: \"dealers\", \"territories\", \"suppression\" or \"warranty\".")]
        string kind,
        [Description("The file to read, for example \"C:\\\\Users\\\\a\\\\Documents\\\\Prospect Studio\\\\Dealers\\\\dealers.csv\". Omit it to use the matching file in the workspace folder for that kind.")]
        string? path = null,
        [Description("The suppression reason for rows whose own reason column is blank: \"customer\", \"dnc\", \"dealer\", \"competitor\" or \"other\". Ignored for the other kinds.")]
        string? reason = null,
        [Description("Treat the suppression file as the whole list: rows it no longer names are deleted and reported as \"removed\", so a company taken off the list stops being suppressed. Off by default, which only ever adds. Ignored for the other kinds.")]
        bool replace = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResults.Ok(
                await lists.ImportAsync(kind, path, reason, replace, cancellationToken).ConfigureAwait(false));
        }
        catch (ImportListRequestException exception)
        {
            throw new McpToolException(ToolErrorCodes.ValidationFailed, exception.Message, exception.Hint);
        }
        catch (ImportListFileNotFoundException exception)
        {
            throw new McpToolException(ToolErrorCodes.NotFound, exception.Message, exception.Hint);
        }
        catch (ImportListUnsupportedException exception)
        {
            throw new McpToolException(ToolErrorCodes.Unsupported, exception.Message, exception.Hint);
        }
        catch (IOException)
        {
            throw new McpToolException(
                ToolErrorCodes.FileLocked,
                $"The {kind} list could not be read.",
                "Close the file in Excel or your spreadsheet app and try again.");
        }
    }

    [McpServerTool(Name = "list_dealers")]
    [Description("Lists the dealers that have been imported, each with how many branches and how many territory rows it has. Use it to check that import_list ran, to see which dealer ids exist before resolving a dealer's territory, or to report who leads are being routed to.")]
    public async ValueTask<CallToolResult> ListDealersAsync(CancellationToken cancellationToken = default) =>
        ToolResults.Ok(new DealerListResult(
            await dealers.ListDealersAsync(cancellationToken).ConfigureAwait(false)));
}

/// <summary>The output of <c>list_dealers</c>: <c>{ "dealers": [...] }</c> (mcp-tools.md §list_dealers).</summary>
public sealed record DealerListResult(IReadOnlyList<DealerSummary> Dealers);
