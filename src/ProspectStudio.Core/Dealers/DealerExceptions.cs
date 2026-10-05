namespace ProspectStudio.Core.Dealers;

/// <summary>
/// The caller asked <c>import_list</c> for something it cannot do - a kind it does not know, a file
/// whose headers are not the documented ones, or a default path that could mean several files. The MCP
/// layer maps this to <c>VALIDATION_FAILED</c>.
/// </summary>
public sealed class ImportListRequestException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>
/// The file <c>import_list</c> was told to read, or the one the workspace default pointed at, is not
/// there. The MCP layer maps this to <c>NOT_FOUND</c>: reporting "imported 0, errors none" would read
/// as "your file is empty" and send the user looking in the wrong place.
/// </summary>
public sealed class ImportListFileNotFoundException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>A list kind a later chunk imports, which the tool reports as <c>UNSUPPORTED</c>.</summary>
public sealed class ImportListUnsupportedException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>
/// One row is wrong. The importer catches it, records an <see cref="ImportRowError"/> and carries on
/// with the rest of the file (mcp-tools.md §import_list): a single bad row must not roll back the
/// marketer's whole list.
/// </summary>
public sealed class ImportRowException(string message) : Exception(message);
