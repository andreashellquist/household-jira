using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Household.Api;

public record ImportRequest(string Url);

public record ImportedRecipe(string? Name, string? Instructions, List<RecipeIngredient> Ingredients);

/// <summary>
/// Imports a recipe from a URL by reading the page's schema.org Recipe JSON-LD
/// (the structured data most recipe sites embed). Falls back to nothing if absent.
/// </summary>
public static partial class RecipeImporter
{
    // Map common Swedish unit spellings onto the canonical units the app uses.
    private static readonly Dictionary<string, string> UnitSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dl"] = "dl", ["deciliter"] = "dl",
        ["l"] = "l", ["liter"] = "l",
        ["msk"] = "msk", ["matsked"] = "msk", ["matskedar"] = "msk",
        ["tsk"] = "tsk", ["tesked"] = "tsk", ["teskedar"] = "tsk",
        ["krm"] = "krm", ["kryddmått"] = "krm",
        ["kg"] = "kg", ["kilo"] = "kg", ["kilogram"] = "kg",
        ["g"] = "g", ["gram"] = "g", ["hg"] = "g",
        ["st"] = "st", ["styck"] = "st", ["stycken"] = "st",
        ["förp"] = "förp", ["förpackning"] = "förp", ["paket"] = "förp", ["pkt"] = "förp",
        ["klyfta"] = "klyfta", ["klyftor"] = "klyfta",
        ["näve"] = "näve", ["nävar"] = "näve",
    };

    public static bool IsAllowed(string url, out string error)
    {
        error = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Ogiltig länk.";
            return false;
        }
        try
        {
            var addresses = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? new[] { IPAddress.Loopback }
                : Dns.GetHostAddresses(uri.Host);
            if (addresses.Any(IsPrivate)) { error = "Otillåten värd."; return false; }
        }
        catch
        {
            error = "Kunde inte slå upp värden.";
            return false;
        }
        return true;
    }

    public static async Task<ImportedRecipe?> Import(string url, HttpClient http)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; HemmaBot/1.0; +recipe-import)");
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;

        var html = await res.Content.ReadAsStringAsync();
        return ExtractFromHtml(html);
    }

    /// <summary>Pulls a recipe out of already-fetched HTML (the network-free, testable core).</summary>
    public static ImportedRecipe? ExtractFromHtml(string html)
    {
        var recipe = FindRecipeNode(html);
        if (recipe is null) return null;

        var name = GetString(recipe.Value, "name");

        var ingredients = new List<RecipeIngredient>();
        if (recipe.Value.TryGetProperty("recipeIngredient", out var ing) && ing.ValueKind == JsonValueKind.Array)
            foreach (var line in ing.EnumerateArray())
                if (line.ValueKind == JsonValueKind.String)
                    ingredients.Add(ParseIngredient(Clean(line.GetString()!)));

        var instructions = recipe.Value.TryGetProperty("recipeInstructions", out var instr)
            ? string.Join("\n", FlattenInstructions(instr)).Trim()
            : null;

        return new ImportedRecipe(name, string.IsNullOrWhiteSpace(instructions) ? null : instructions, ingredients);
    }

    // ---- JSON-LD discovery ----
    private static JsonElement? FindRecipeNode(string html)
    {
        foreach (Match m in LdJsonBlock().Matches(html))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(m.Groups[1].Value); }
            catch { continue; }
            if (Search(doc.RootElement) is { } found) return found;
        }
        return null;
    }

    private static JsonElement? Search(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (Search(item) is { } f) return f;
                break;
            case JsonValueKind.Object:
                if (IsRecipeType(el)) return el;
                if (el.TryGetProperty("@graph", out var graph) && Search(graph) is { } g) return g;
                break;
        }
        return null;
    }

    private static bool IsRecipeType(JsonElement obj)
    {
        if (!obj.TryGetProperty("@type", out var type)) return false;
        if (type.ValueKind == JsonValueKind.String)
            return type.GetString()!.Contains("Recipe", StringComparison.OrdinalIgnoreCase);
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String &&
                t.GetString()!.Contains("Recipe", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    // recipeInstructions may be a string, an array of strings, HowToStep objects, or HowToSection groups.
    private static IEnumerable<string> FlattenInstructions(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                foreach (var part in SplitText(Clean(el.GetString()!))) yield return part;
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    foreach (var s in FlattenInstructions(item)) yield return s;
                break;
            case JsonValueKind.Object:
                if (el.TryGetProperty("itemListElement", out var steps))
                    foreach (var s in FlattenInstructions(steps)) yield return s;
                else if (el.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    yield return Clean(text.GetString()!);
                else if (el.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                    yield return Clean(nm.GetString()!);
                break;
        }
    }

    // ---- Ingredient parsing: "3 dl vetemjöl" -> { amount: 3, unit: "dl", name: "vetemjöl" } ----
    public static RecipeIngredient ParseIngredient(string raw)
    {
        var s = WhitespaceRun().Replace(raw, " ").Trim();
        var (amount, rest) = ParseQuantity(s);

        var unit = "st";
        var tokens = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0)
        {
            var head = tokens[0].Trim('.', ',').ToLowerInvariant();
            if (UnitSynonyms.TryGetValue(head, out var canonical))
            {
                unit = canonical;
                rest = tokens.Length > 1 ? tokens[1] : "";
            }
        }

        var name = rest.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = raw.Trim();
        return new RecipeIngredient { Name = name, Amount = amount, Unit = unit };
    }

    private static (double Amount, string Remainder) ParseQuantity(string s)
    {
        // Mixed/simple fraction: "1 1/2", "1/2"
        var frac = Regex.Match(s, @"^(?:(\d+)\s+)?(\d+)\s*/\s*(\d+)");
        if (frac.Success)
        {
            double whole = frac.Groups[1].Success ? double.Parse(frac.Groups[1].Value) : 0;
            double num = double.Parse(frac.Groups[2].Value), den = double.Parse(frac.Groups[3].Value);
            return (whole + (den != 0 ? num / den : 0), s[frac.Length..].TrimStart());
        }
        // Decimal/integer with Swedish comma or dot
        var dec = Regex.Match(s, @"^\d+(?:[.,]\d+)?");
        if (dec.Success)
            return (double.Parse(dec.Value.Replace(',', '.'), CultureInfo.InvariantCulture), s[dec.Length..].TrimStart());
        // Unicode fraction glyph
        if (s.Length > 0 && UnicodeFraction(s[0]) is > 0 and var f)
            return (f, s[1..].TrimStart());
        return (0, s);
    }

    private static double UnicodeFraction(char c) => c switch
    {
        '½' => 0.5, '¼' => 0.25, '¾' => 0.75,
        '⅓' => 1.0 / 3, '⅔' => 2.0 / 3,
        '⅛' => 0.125, '⅜' => 0.375, '⅝' => 0.625, '⅞' => 0.875,
        _ => 0,
    };

    // ---- helpers ----
    private static IEnumerable<string> SplitText(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Clean(string s) =>
        WebUtility.HtmlDecode(WhitespaceRun().Replace(HtmlTag().Replace(s, " "), " ")).Trim();

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString()!) : null;

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254); // link-local
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || (b[0] & 0xfe) == 0xfc; // ULA fc00::/7
        return false;
    }

    [GeneratedRegex("<script[^>]*type=[\"']application/ld\\+json[\"'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LdJsonBlock();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
