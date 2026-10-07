using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TemplateName.IntegrationTests.Documentation;

/// <summary>
/// Compares a JSON document with a committed <c>*.verified.json</c> snapshot (ADR 0011). On a mismatch it writes the current document to
/// the git-ignored <c>*.received.json</c> beside it, so accepting an intended change is a file rename and a commit.
/// </summary>
public static class JsonSnapshot
{
    private static readonly JsonSerializerOptions Formatting = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Indents <paramref name="json"/> (two spaces, LF line endings, a final newline) and removes the named top-level members. The
    /// document's own property order is kept.
    /// </summary>
    public static string Normalize(string json, params string[] ignoredMembers)
    {
        var document = JsonNode.Parse(json) ?? throw new ArgumentException("The JSON document is empty.", nameof(json));
        if (document is JsonObject root)
        {
            foreach (var member in ignoredMembers)
            {
                root.Remove(member);
            }
        }

        return document.ToJsonString(Formatting) + "\n";
    }

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="normalizedJson"/> equals the snapshot at <paramref name="verifiedPath"/>
    /// (and deletes a stale received file); otherwise writes the received file and returns a message naming both files.
    /// </summary>
    public static string? Compare(string normalizedJson, string verifiedPath)
    {
        if (!Path.GetFileName(verifiedPath).Contains(".verified.", StringComparison.Ordinal))
        {
            throw new ArgumentException("The snapshot file name must contain '.verified.'.", nameof(verifiedPath));
        }

        var receivedPath = verifiedPath.Replace(".verified.", ".received.", StringComparison.Ordinal);
        var isVerifiedPresent = File.Exists(verifiedPath);

        if (isVerifiedPresent && File.ReadAllText(verifiedPath).ReplaceLineEndings("\n") == normalizedJson)
        {
            File.Delete(receivedPath);
            return null;
        }

        File.WriteAllText(receivedPath, normalizedJson);
        var problem = isVerifiedPresent ? $"The document differs from the snapshot {verifiedPath}." : $"The snapshot {verifiedPath} does not exist.";
        return $"{problem} The current document was written to {receivedPath}. To accept an intended API change, replace the .verified.json "
            + "with the .received.json and commit it.";
    }
}
