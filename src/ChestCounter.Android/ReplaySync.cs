using Android.Content;
using Android.Provider;

namespace ChestCounter.Droid;

/// <summary>
/// Brings replay files recorded by Fortnite on a PC into the app's folder (Fortnite on Android records none): from a
/// folder the player granted with the system picker (Storage Access Framework), kept in sync with the PC for
/// example, or picked / shared by hand. Files are opened read-only.
/// </summary>
public static class ReplaySync
{
    public const string FortnitePackage = "com.epicgames.fortnite";
    private const string Prefs = "replay_sync";
    private const string TreeKey = "tree_uri";

    public enum Result { Synced, NoFolder, Lost }

    public static Android.Net.Uri? GrantedTree(Context c)
    {
        var s = c.GetSharedPreferences(Prefs, FileCreationMode.Private)!.GetString(TreeKey, null);
        return s == null ? null : Android.Net.Uri.Parse(s);
    }

    public static void SaveTree(Context c, Android.Net.Uri tree)
    {
        c.ContentResolver!.TakePersistableUriPermission(tree, ActivityFlags.GrantReadUriPermission);
        c.GetSharedPreferences(Prefs, FileCreationMode.Private)!.Edit()!.PutString(TreeKey, tree.ToString()).Apply();
    }

    public static Result Sync(Context c, string destFolder)
    {
        var tree = GrantedTree(c);
        if (tree == null)
        {
            return Result.NoFolder;
        }
        if (!c.ContentResolver!.PersistedUriPermissions.Any(p => p.Uri?.Equals(tree) == true && p.IsReadPermission))
        {
            return Result.Lost;
        }
        try
        {
            var rootDoc = DocumentsContract.GetTreeDocumentId(tree)!;
            foreach (var doc in FindReplays(c, tree, rootDoc, 0))
            {
                Copy(() => c.ContentResolver!.OpenInputStream(doc.Uri)!, doc.Name, doc.Size, doc.Modified, destFolder);
            }
            return Result.Synced;
        }
        catch (Java.Lang.SecurityException)
        {
            return Result.Lost;
        }
    }

    private sealed record Doc(Android.Net.Uri Uri, string Name, long Size, DateTime Modified);

    /// <summary>Every .replay file under the granted folder (the player may pick Demos itself or a parent of it).</summary>
    private static IEnumerable<Doc> FindReplays(Context c, Android.Net.Uri tree, string docId, int depth)
    {
        if (depth > 7)
        {
            yield break;
        }
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, docId)!;
        var cols = new[]
        {
            DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType, DocumentsContract.Document.ColumnSize,
            DocumentsContract.Document.ColumnLastModified,
        };
        var found = new List<Doc>();
        var folders = new List<string>();
        using (var cursor = c.ContentResolver!.Query(children, cols, null, null, null))
        {
            while (cursor != null && cursor.MoveToNext())
            {
                var id = cursor.GetString(0)!;
                var name = cursor.GetString(1) ?? "";
                var mime = cursor.GetString(2);
                if (mime == DocumentsContract.Document.MimeTypeDir)
                {
                    if (!name.StartsWith('.'))
                    {
                        folders.Add(id);
                    }
                }
                else if (name.EndsWith(".replay", StringComparison.OrdinalIgnoreCase))
                {
                    var modified = DateTimeOffset.FromUnixTimeMilliseconds(cursor.IsNull(4) ? 0 : cursor.GetLong(4)).UtcDateTime;
                    found.Add(new Doc(DocumentsContract.BuildDocumentUriUsingTree(tree, id)!, name, cursor.IsNull(3) ? -1 : cursor.GetLong(3), modified));
                }
            }
        }
        foreach (var d in found)
        {
            yield return d;
        }
        foreach (var f in folders)
        {
            foreach (var d in FindReplays(c, tree, f, depth + 1))
            {
                yield return d;
            }
        }
    }

    /// <summary>A replay picked or shared by hand.</summary>
    public static bool CopyDocument(Context c, Android.Net.Uri uri, string destFolder)
    {
        string name = "imported.replay";
        long size = -1;
        var modified = DateTime.UtcNow;
        using (var cursor = c.ContentResolver!.Query(uri, null, null, null, null))
        {
            if (cursor != null && cursor.MoveToFirst())
            {
                var n = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                var s = cursor.GetColumnIndex(IOpenableColumns.Size);
                var m = cursor.GetColumnIndex(DocumentsContract.Document.ColumnLastModified);
                if (n >= 0 && !cursor.IsNull(n)) name = cursor.GetString(n)!;
                if (s >= 0 && !cursor.IsNull(s)) size = cursor.GetLong(s);
                if (m >= 0 && !cursor.IsNull(m)) modified = DateTimeOffset.FromUnixTimeMilliseconds(cursor.GetLong(m)).UtcDateTime;
            }
        }
        if (!name.EndsWith(".replay", StringComparison.OrdinalIgnoreCase))
        {
            name += ".replay";
        }
        return Copy(() => c.ContentResolver!.OpenInputStream(uri)!, name, size, modified, destFolder);
    }

    /// <summary>
    /// Copies a replay unless the copy is already the same. The copy keeps the original's date, so that the counter
    /// sees the same file (and the same "finished a while ago") as on the PC.
    /// </summary>
    private static bool Copy(Func<Stream> open, string name, long size, DateTime modifiedUtc, string destFolder)
    {
        name = string.Concat(Path.GetFileName(name).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dest = new FileInfo(Path.Combine(destFolder, name));
        if (dest.Exists && dest.Length == size && Math.Abs((dest.LastWriteTimeUtc - modifiedUtc).TotalSeconds) < 2)
        {
            return false;
        }
        // Being written right now (a match in progress): wait for the next pass.
        if (DateTime.UtcNow - modifiedUtc < TimeSpan.FromSeconds(5))
        {
            return false;
        }
        var tmp = dest.FullName + ".part";
        using (var input = open())
        using (var output = File.Create(tmp))
        {
            input.CopyTo(output);
        }
        File.Move(tmp, dest.FullName, overwrite: true);
        if (modifiedUtc > DateTime.UnixEpoch)
        {
            File.SetLastWriteTimeUtc(dest.FullName, modifiedUtc);
        }
        return true;
    }
}
