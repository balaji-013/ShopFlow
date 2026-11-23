using System.Text;
using System.Text.Json;

namespace ShopFlow.Application.Common;

/// <summary>
/// KEYSET (cursor) PAGINATION. OFFSET n forces SQL Server to read and discard n rows (O(n)), and shifts when rows are inserted
/// during paging (duplicates / skips). A cursor of (CreatedAt, Id) seeks straight to the position via the index: O(log n) and stable.
/// The cursor is opaque base64 so clients cannot depend on its shape.
/// </summary>
public static class Cursor
{
    private record Payload(DateTime T, Guid Id);

    public static string Encode(DateTime createdAt, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Payload(createdAt, id))));

    public static bool TryDecode(string? cursor, out DateTime createdAt, out Guid id)
    {
        createdAt = default; id = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var p = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));
            if (p is null || p.Id == Guid.Empty) return false;   // "{}" deserialises to defaults - reject it
            createdAt = p.T; id = p.Id; return true;
        }
        catch { return false; }
    }
}

