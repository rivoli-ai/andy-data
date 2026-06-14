namespace Andy.Data;

/// <summary>
/// Optional host-defined policy that constrains which filesystem paths the dataframe tools may
/// read from or write to. Implementations can enforce allow-lists, block-lists, sandbox roots,
/// or any custom logic. When no implementation is registered, all paths are permitted and the
/// existing behavior is unchanged.
/// </summary>
public interface IPathPolicy
{
    /// <summary>
    /// Returns <c>true</c> if the tool is allowed to read from <paramref name="path"/>.
    /// The path may be a concrete file/directory or a glob pattern.
    /// </summary>
    bool CanRead(string path);

    /// <summary>
    /// Returns <c>true</c> if the tool is allowed to write to <paramref name="path"/>.
    /// </summary>
    bool CanWrite(string path);
}
