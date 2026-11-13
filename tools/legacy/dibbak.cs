#:package MongoDB.Bson@3.13.0
#:package BCrypt.Net-Next@4.2.1
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Phase 0 (PLAN.md §4.5, §10): tools for legacy .dibbak backups, read with the .NET BSON library.
//
//   dotnet run tools/legacy/dibbak.cs -- inventory <file.dibbak> [--md out.md] [--json out.json]
//       Data-shape inventory: for every collection and field path, how often it is present, its BSON
//       types, Int32/Double mixes, string shapes (date-like, numeric, padded, non-NFC), date ranges,
//       array lengths. Prints VALUES only for known enum fields; everything else is counts, so the
//       output contains no personal or business data and can be committed.
//
//   dotnet run tools/legacy/dibbak.cs -- anonymize <in.dibbak> <out.dibbak> [--password dib4t-dev]
//       Anonymized copy, safe by default: every string is scrambled EXCEPT an allowlist (enum fields,
//       budgetNumber). Scrambling keeps length, case, spaces, punctuation, digit/letter classes and
//       accented-ness, and is deterministic per value under a random key that is never stored, so equal
//       values stay equal across collections (createdBy ↔ users.name, paymentMethod ↔ paymentmethods.name)
//       and nothing can be reversed. Users get emails utilizadorNN@dib4t.local and one known password.
//       ObjectIds, numbers (exact BSON types), dates, booleans, nulls, field order and unknown fields are
//       kept. Ends with a leak check: no output string may equal or contain an original scrambled value.
//
// Format (back-end/helpers/backupFormat.js): gzip( "DIBBAK01" + frames ), frame = [type u8][len u32 LE][payload]
//   H header JSON · C collection JSON {name, options, indexes, count} · D document BSON · Z end JSON.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length < 2) { Console.Error.WriteLine("usage: dibbak.cs inventory <file.dibbak> [--md out.md] [--json out.json]"); return 1; }
var opts = new Dictionary<string, string>();
for (var i = 2; i < args.Length - 1; i++) if (args[i].StartsWith("--")) opts[args[i][2..]] = args[++i];

switch (args[0])
{
    case "inventory":
    {
        var backup = Dibbak.Read(args[1]);
        var inv = Inventory.Build(backup);
        var md = Inventory.Markdown(inv, backup, Path.GetFileName(args[1]));
        if (opts.TryGetValue("md", out var mdPath)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mdPath))!); File.WriteAllText(mdPath, md); Console.WriteLine($"wrote {mdPath}"); }
        else Console.WriteLine(md);
        if (opts.TryGetValue("json", out var jsonPath)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!); File.WriteAllText(jsonPath, Inventory.Json(inv, backup)); Console.WriteLine($"wrote {jsonPath}"); }
        return 0;
    }
    case "anonymize":
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: dibbak.cs anonymize <in.dibbak> <out.dibbak> [--password pw]"); return 1; }
        var backup = Dibbak.Read(args[1]);
        var password = opts.GetValueOrDefault("password", "dib4t-dev");
        var report = Anonymizer.Run(backup, password);
        Dibbak.Write(args[2], backup);
        Console.WriteLine(report);
        // Round-trip check: the written file must read back with the same counts.
        var again = Dibbak.Read(args[2]);
        Console.WriteLine($"re-read ok: {again.Collections.Count} collections, {again.Collections.Sum(c => c.Documents.Count)} documents");
        return report.Contains("LEAK") ? 2 : 0;
    }
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 1;
}

// ===================================================================== .dibbak reader
record Collection(string Name, JsonNode Meta, List<BsonDocument> Documents);
record Backup(JsonNode Header, List<Collection> Collections, JsonNode End);

static class Dibbak
{
    public static readonly byte[] Magic = "DIBBAK01"u8.ToArray();

    public static Backup Read(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var buf = ms.ToArray();
        if (!buf.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("not a .dibbak file");
        var off = Magic.Length;
        JsonNode? header = null, end = null;
        var cols = new List<Collection>();
        while (off < buf.Length)
        {
            var type = (char)buf[off];
            var len = BitConverter.ToInt32(buf, off + 1);
            var payload = buf.AsSpan(off + 5, len);
            off += 5 + len;
            switch (type)
            {
                case 'H': header = JsonNode.Parse(payload); break;
                case 'C': var meta = JsonNode.Parse(payload)!; cols.Add(new Collection(meta["name"]!.GetValue<string>(), meta, [])); break;
                case 'D':
                    // Raw BSON as written by the Node driver; read without any class map so every type survives.
                    using (var reader = new BsonBinaryReader(new MemoryStream(payload.ToArray())))
                        cols[^1].Documents.Add(BsonSerializer.Deserialize<BsonDocument>(reader));
                    break;
                case 'Z': end = JsonNode.Parse(payload); break;
                default: throw new InvalidDataException($"unknown frame '{type}'");
            }
        }
        if (header is null || end is null) throw new InvalidDataException("missing header or end frame");
        var n = cols.Sum(c => c.Documents.Count);
        if (end["documents"]?.GetValue<int>() != n) throw new InvalidDataException($"end frame says {end["documents"]} documents, read {n}");
        return new Backup(header, cols, end);
    }

    // Same layout as the legacy BackupWriter: gzip("DIBBAK01" + H + (C + D*)* + Z).
    public static void Write(string path, Backup b)
    {
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Optimal);
        gz.Write(Magic);
        void Frame(char type, byte[] payload)
        {
            gz.WriteByte((byte)type);
            gz.Write(BitConverter.GetBytes(payload.Length));
            gz.Write(payload);
        }
        byte[] Json(JsonNode n) => Encoding.UTF8.GetBytes(n.ToJsonString());
        Frame('H', Json(b.Header));
        foreach (var c in b.Collections)
        {
            Frame('C', Json(c.Meta));
            foreach (var d in c.Documents) Frame('D', d.ToBson());
        }
        Frame('Z', Json(b.End));
    }
}

// ===================================================================== anonymizer
static class Anonymizer
{
    // Kept verbatim (matched on the last path segment): fixed vocabularies + budget numbers.
    static readonly HashSet<string> Keep =
    [
        "state", "priority", "taskType", "templateName", "type", "roles[]", "fieldOfCompetencies", "commentType[]",
        "validTryOnFirstTest", "verificationAndShapeControl", "dimensionalVerificationAndControl", "analysisOfTheDegreeOfFinish",
        "pneumaticCircuits", "electricalBoardsAndCircuits", "kinematics", "programmingFeaturesPLC",
        "operator_equipmentSafetyConditions", "visualInspection", "budgetNumber",
    ];
    const string LowerAccents = "áàâãéêíóôõúç", UpperAccents = "ÁÀÂÃÉÊÍÓÔÕÚÇ";

    public static string Run(Backup b, string password)
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32); // never stored
        var originals = new HashSet<string>(StringComparer.Ordinal);
        var scrambledCount = 0;

        string Scramble(string v)
        {
            if (!v.Any(char.IsLetterOrDigit)) return v;
            var t = v.Trim();
            if (t.Length >= 4) originals.Add(t);
            scrambledCount++;
            var seed = BitConverter.ToInt32(System.Security.Cryptography.HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(v)), 0);
            var rnd = new Random(seed);
            var sb = new StringBuilder(v.Length);
            for (var k = 0; k < v.Length; k++)
            {
                var ch = v[k];
                // A base letter followed by a combining mark is kept, so non-NFC strings stay non-NFC (§4.3 tests).
                if (k + 1 < v.Length && CharUnicodeInfo.GetUnicodeCategory(v[k + 1]) == UnicodeCategory.NonSpacingMark) sb.Append(ch);
                else if (char.IsDigit(ch)) sb.Append((char)('0' + rnd.Next(10)));
                else if (ch is >= 'a' and <= 'z') sb.Append((char)('a' + rnd.Next(26)));
                else if (ch is >= 'A' and <= 'Z') sb.Append((char)('A' + rnd.Next(26)));
                else if (char.IsLetter(ch)) sb.Append(char.IsUpper(ch) ? UpperAccents[rnd.Next(UpperAccents.Length)] : LowerAccents[rnd.Next(LowerAccents.Length)]);
                else sb.Append(ch); // spaces, punctuation, tabs, newlines, combining marks
            }
            return sb.ToString();
        }

        BsonValue Visit(string path, BsonValue v) => v.BsonType switch
        {
            BsonType.String => Keep.Contains(path[(path.LastIndexOf('.') + 1)..]) ? v : new BsonString(Scramble(v.AsString)),
            BsonType.Document => VisitDoc(path + ".", v.AsBsonDocument),
            BsonType.Array => new BsonArray(v.AsBsonArray.Select(x => Visit(path + "[]", x))),
            _ => v, // ObjectId, numbers (exact type), dates, booleans, null: unchanged
        };
        BsonDocument VisitDoc(string prefix, BsonDocument d)
        {
            var o = new BsonDocument();
            foreach (var el in d) o.Add(el.Name, Visit(prefix + el.Name, el.Value)); // same field order
            return o;
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(password, 10);
        var users = new List<string>();
        foreach (var c in b.Collections)
        {
            for (var i = 0; i < c.Documents.Count; i++)
            {
                var d = VisitDoc("", c.Documents[i]);
                if (c.Name == "users")
                {
                    var email = $"utilizador{i + 1:00}@dib4t.local";
                    originals.Add(c.Documents[i]["email"].AsString);
                    d["email"] = email;
                    d["password"] = hash;
                    users.Add($"  {email}  roles: {string.Join(",", d["roles"].AsBsonArray.Select(r => r.IsBsonNull ? "null" : r.AsString))}  field: {(d.Contains("fieldOfCompetencies") ? d["fieldOfCompetencies"] : "(missing)")}");
                }
                if (c.Name == "printersettings") d["ip"] = $"127.0.0.{i + 1}";
                c.Documents[i] = d;
            }
        }
        if (b.Header["createdBy"] is not null) { originals.Add(b.Header["createdBy"]!.ToString()); b.Header["createdBy"] = "anonimizado@dib4t.local"; }

        // Leak check: no output string equals or contains an original scrambled value (≥ 4 chars).
        // A match is NOT a leak when the matching text is itself a kept vocabulary value (e.g. a task
        // description that was literally "Montagem" matches the kept job state "Montagem").
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in b.Collections) foreach (var d in c.Documents) foreach (var (path, s) in Strings("", d))
            if (Keep.Contains(path[(path.LastIndexOf('.') + 1)..])) kept.Add(s.Trim());
        var leaks = 0;
        var byPath = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var longOriginals = originals.Where(o => o.Length >= 6 && !kept.Contains(o)).ToList();
        foreach (var c in b.Collections)
            foreach (var d in c.Documents)
                foreach (var (path, s) in Strings(c.Name + ".", d))
                {
                    var t = s.Trim();
                    if (kept.Contains(t)) continue;
                    var hit = (t.Length >= 4 && originals.Contains(t)) || (t.Length >= 6 && longOriginals.Any(o => t.Contains(o, StringComparison.Ordinal)));
                    if (hit) { leaks++; byPath[path] = byPath.GetValueOrDefault(path) + 1; }
                }
        var leakPaths = string.Join(", ", byPath.Select(kv => $"{kv.Key} ×{kv.Value}"));

        var leakLine = leaks == 0 ? "OK, no original value found in the output (vocabulary values excluded)" : $"LEAK: {leaks} output strings contain original values ({leakPaths})";
        return string.Join(Environment.NewLine, new[]
        {
            $"scrambled {scrambledCount} string values ({originals.Count} distinct originals checked)",
            $"password for all users: {password}  (bcrypt cost 10, {hash[..4]})",
            "users:",
        }.Concat(users).Append($"leak check: {leakLine}"));
    }

    static IEnumerable<(string Path, string Value)> Strings(string path, BsonValue v) => v.BsonType switch
    {
        BsonType.String => [(path.TrimEnd('.'), v.AsString)],
        BsonType.Document => v.AsBsonDocument.SelectMany(e => Strings(path + e.Name + (e.Value.IsBsonDocument ? "." : ""), e.Value)),
        BsonType.Array => v.AsBsonArray.SelectMany(x => Strings(path.TrimEnd('.') + "[]" + (x.IsBsonDocument ? "." : ""), x)),
        _ => [],
    };
}

// ===================================================================== inventory
class PathStats
{
    public int Present;          // documents (or array elements' parents) where the path exists
    public SortedDictionary<string, int> Types = new();
    public int Null;
    // numbers
    public int IntegralDoubles, FractionalDoubles, Negative, NaNOrInfinity;
    // strings
    public int Empty, Padded, NonNfc, DateLike, NumericLike, MinLen = int.MaxValue, MaxLen;
    public SortedDictionary<string, int> StringShapes = new();
    public SortedDictionary<string, int>? EnumValues;
    // dates
    public DateTime? MinDate, MaxDate;
    // arrays
    public int EmptyArrays, MaxArrayLen;
}

static class Inventory
{
    // Fields whose values are fixed vocabularies (safe to print). Matched on the last path segment.
    static readonly HashSet<string> EnumFields =
    [
        "state", "priority", "taskType", "templateName", "type", "roles[]", "fieldOfCompetencies", "commentType[]",
        "validTryOnFirstTest", "verificationAndShapeControl", "dimensionalVerificationAndControl", "analysisOfTheDegreeOfFinish",
        "pneumaticCircuits", "electricalBoardsAndCircuits", "kinematics", "programmingFeaturesPLC",
        "operator_equipmentSafetyConditions", "visualInspection",
        // NOT paymentMethod/paymentCondition/paymentmethods.name: free text that can name customers.
    ];
    static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}([T ][\d:.]+Z?([+-]\d{2}:?\d{2})?)?$", RegexOptions.Compiled);
    static readonly Regex PtDate = new(@"^\d{1,2}/\d{1,2}/\d{2,4}$", RegexOptions.Compiled);
    static readonly Regex Numeric = new(@"^-?\d+([.,]\d+)?$", RegexOptions.Compiled);

    public static Dictionary<string, (int Docs, SortedDictionary<string, PathStats> Paths)> Build(Backup b)
    {
        var result = new Dictionary<string, (int, SortedDictionary<string, PathStats>)>();
        foreach (var c in b.Collections)
        {
            var paths = new SortedDictionary<string, PathStats>(StringComparer.Ordinal);
            foreach (var d in c.Documents) Walk(d, "", paths);
            result[c.Name] = (c.Documents.Count, paths);
        }
        return result;
    }

    static void Walk(BsonDocument doc, string prefix, SortedDictionary<string, PathStats> paths)
    {
        foreach (var el in doc) Visit(prefix + el.Name, el.Value, paths);
    }

    static void Visit(string path, BsonValue v, SortedDictionary<string, PathStats> paths)
    {
        if (!paths.TryGetValue(path, out var s)) paths[path] = s = new PathStats();
        s.Present++;
        var t = v.BsonType.ToString();
        s.Types[t] = s.Types.GetValueOrDefault(t) + 1;
        var leaf = path[(path.LastIndexOf('.') + 1)..];
        switch (v.BsonType)
        {
            case BsonType.Null: s.Null++; break;
            case BsonType.Double:
                var dbl = v.AsDouble;
                if (double.IsNaN(dbl) || double.IsInfinity(dbl)) s.NaNOrInfinity++;
                else if (Math.Floor(dbl) == dbl) s.IntegralDoubles++; else s.FractionalDoubles++;
                if (dbl < 0) s.Negative++;
                break;
            case BsonType.Int32: if (v.AsInt32 < 0) s.Negative++; break;
            case BsonType.Int64: if (v.AsInt64 < 0) s.Negative++; break;
            case BsonType.String:
                var str = v.AsString;
                if (str.Length == 0) s.Empty++;
                if (str.Length > 0 && str.Trim().Length != str.Length) s.Padded++;
                if (!str.IsNormalized(NormalizationForm.FormC)) s.NonNfc++;
                if (IsoDate.IsMatch(str) || PtDate.IsMatch(str)) s.DateLike++;
                if (Numeric.IsMatch(str)) s.NumericLike++;
                s.MinLen = Math.Min(s.MinLen, str.Length); s.MaxLen = Math.Max(s.MaxLen, str.Length);
                var shape = Shape(str);
                s.StringShapes[shape] = s.StringShapes.GetValueOrDefault(shape) + 1;
                if (EnumFields.Contains(leaf)) { s.EnumValues ??= new(StringComparer.Ordinal); s.EnumValues[str] = s.EnumValues.GetValueOrDefault(str) + 1; }
                break;
            case BsonType.DateTime:
                var dt = v.ToUniversalTime();
                if (s.MinDate is null || dt < s.MinDate) s.MinDate = dt;
                if (s.MaxDate is null || dt > s.MaxDate) s.MaxDate = dt;
                break;
            case BsonType.Document: Walk(v.AsBsonDocument, path + ".", paths); break;
            case BsonType.Array:
                var arr = v.AsBsonArray;
                if (arr.Count == 0) s.EmptyArrays++;
                s.MaxArrayLen = Math.Max(s.MaxArrayLen, arr.Count);
                foreach (var item in arr) Visit(path + "[]", item, paths);
                break;
        }
    }

    // Character-class shape of a string, collapsed (e.g. "26-0001" → "99-9999", "MR_M7791" → "AA_A9999").
    // Only kept when short and structured (ids, numbers); free text becomes "text".
    static string Shape(string s)
    {
        if (s.Length == 0) return "(empty)";
        if (s.Length > 12 || s.Contains(' ')) return "text";
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(char.IsDigit(ch) ? '9' : char.IsLetter(ch) ? 'A' : ch);
        var shape = sb.ToString();
        return shape.Count(c => c == 'A') > 3 ? "word" : shape;
    }

    public static string Markdown(Dictionary<string, (int Docs, SortedDictionary<string, PathStats> Paths)> inv, Backup b, string fileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Legacy data-shape inventory (§4.5)");
        sb.AppendLine();
        sb.AppendLine($"> Generated by `dotnet run tools/legacy/dibbak.cs -- inventory` from the production backup `{fileName}` " +
                      $"(created {b.Header["createdAt"]}, server {b.Header["serverVersion"]}). Do not edit by hand.");
        sb.AppendLine("> Contains **no data values** except for fixed vocabularies (enum fields). Counts are documents (or array elements for `[]` paths).");
        sb.AppendLine();
        sb.AppendLine("Columns: **present** = documents having the path (missing = docs − present) · **types** = BSON types seen · " +
                      "**notes** = Int32/Double mix, integral vs fractional doubles, empty/padded/non-NFC/date-like/numeric strings, date range, array sizes.");
        foreach (var (name, (docs, paths)) in inv)
        {
            var meta = b.Collections.First(c => c.Name == name).Meta;
            sb.AppendLine();
            sb.AppendLine($"## {name} ({docs} documents)");
            sb.AppendLine();
            sb.AppendLine($"Indexes: {string.Join(", ", meta["indexes"]!.AsArray().Select(i => $"`{i!["name"]}`"))} · options: `{meta["options"]?.ToJsonString()}`");
            sb.AppendLine();
            sb.AppendLine("| Path | Present | Types | Notes |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var (path, s) in paths)
            {
                var parentCount = ParentCount(path, docs, paths);
                var present = path.EndsWith("[]") ? $"{s.Present} elements"
                    : s.Present == parentCount ? $"all {s.Present}" : $"{s.Present} / {parentCount} (**{parentCount - s.Present} missing**)";
                var types = string.Join(", ", s.Types.Select(t => s.Types.Count > 1 ? $"{t.Key} {t.Value}" : t.Key));
                if (s.Types.Count > 1) types = $"**{types}**";
                sb.AppendLine($"| `{path}` | {present} | {types} | {Notes(s)} |");
            }
            var enums = paths.Where(p => p.Value.EnumValues != null).ToList();
            if (enums.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Enum values:");
                foreach (var (path, s) in enums)
                    sb.AppendLine($"- `{path}`: " + string.Join(", ", s.EnumValues!.Select(e => $"`{e.Key}` {e.Value}")));
            }
        }
        return sb.ToString();
    }

    // How many times the parent of a path exists (documents for top-level, parent presence otherwise).
    static int ParentCount(string path, int docs, SortedDictionary<string, PathStats> paths)
    {
        if (path.EndsWith("[]")) return paths[path[..^2]].Present - paths[path[..^2]].EmptyArrays;
        var dot = path.LastIndexOf('.');
        if (dot < 0) return docs;
        var parent = path[..dot];
        return paths.TryGetValue(parent, out var p) ? p.Types.GetValueOrDefault("Document") : docs;
    }

    static string Notes(PathStats s)
    {
        var n = new List<string>();
        if (s.Null > 0) n.Add($"null {s.Null}");
        if (s.IntegralDoubles > 0 && s.Types.ContainsKey("Double")) n.Add($"integral doubles {s.IntegralDoubles}");
        if (s.FractionalDoubles > 0) n.Add($"fractional {s.FractionalDoubles}");
        if (s.Negative > 0) n.Add($"negative {s.Negative}");
        if (s.NaNOrInfinity > 0) n.Add($"**NaN/∞ {s.NaNOrInfinity}**");
        if (s.Types.ContainsKey("String"))
        {
            n.Add($"len {s.MinLen}–{s.MaxLen}");
            if (s.Empty > 0) n.Add($"empty {s.Empty}");
            if (s.Padded > 0) n.Add($"padded {s.Padded}");
            if (s.NonNfc > 0) n.Add($"**non-NFC {s.NonNfc}**");
            if (s.DateLike > 0) n.Add($"**date-like {s.DateLike}**");
            if (s.NumericLike > 0 && s.EnumValues == null) n.Add($"numeric-like {s.NumericLike}");
            var shapes = s.StringShapes.Where(x => x.Key is not ("text" or "word" or "(empty)")).OrderByDescending(x => x.Value).Take(4).ToList();
            if (shapes.Count > 0 && s.EnumValues == null) n.Add("shapes " + string.Join(" ", shapes.Select(x => $"`{x.Key}`×{x.Value}")));
        }
        if (s.MinDate != null) n.Add($"{s.MinDate:yyyy-MM-dd} … {s.MaxDate:yyyy-MM-dd}");
        if (s.Types.ContainsKey("Array")) n.Add($"empty arrays {s.EmptyArrays}, max len {s.MaxArrayLen}");
        return string.Join(" · ", n);
    }

    public static string Json(Dictionary<string, (int Docs, SortedDictionary<string, PathStats> Paths)> inv, Backup b)
    {
        var root = new JsonObject
        {
            ["source"] = new JsonObject { ["createdAt"] = b.Header["createdAt"]?.DeepClone(), ["serverVersion"] = b.Header["serverVersion"]?.DeepClone() },
        };
        var cols = new JsonObject();
        foreach (var (name, (docs, paths)) in inv)
        {
            var p = new JsonObject();
            foreach (var (path, s) in paths)
            {
                var o = new JsonObject { ["present"] = s.Present, ["types"] = new JsonObject(s.Types.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)t.Value))) };
                if (s.Null > 0) o["null"] = s.Null;
                if (s.Types.ContainsKey("Double")) { o["integralDoubles"] = s.IntegralDoubles; o["fractionalDoubles"] = s.FractionalDoubles; }
                if (s.EnumValues != null) o["enumValues"] = new JsonObject(s.EnumValues.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Value)));
                if (s.DateLike > 0) o["dateLikeStrings"] = s.DateLike;
                if (s.NonNfc > 0) o["nonNfcStrings"] = s.NonNfc;
                if (s.Types.ContainsKey("Array")) { o["emptyArrays"] = s.EmptyArrays; o["maxArrayLength"] = s.MaxArrayLen; }
                p[path] = o;
            }
            cols[name] = new JsonObject { ["documents"] = docs, ["paths"] = p };
        }
        root["collections"] = cols;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
