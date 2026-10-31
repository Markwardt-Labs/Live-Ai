namespace Markwardt.LiveAi;

/// <summary>
/// One entry of a listing of a folder in an <see cref="IStore" />.
/// </summary>
/// <param name="Path">The path of the file or folder relative to the folder that was listed, with <c>/</c> between the names: just the name for something directly in the folder, and for a recursive listing also the folders in between.</param>
/// <param name="IsDirectory">Whether the entry is a folder.</param>
public sealed record StoreEntry(string Path, bool IsDirectory);
