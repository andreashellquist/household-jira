using System.Globalization;

namespace Household.Api;

public enum UnitType { Volume, Mass, Piece }

public class Recipe
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Source { get; set; }       // cookbook page or a URL
    public string? Instructions { get; set; }
    public string? Preparations { get; set; }
    public List<RecipeIngredient> Ingredients { get; set; } = new();
}

public class RecipeIngredient
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public required string Name { get; set; }  // product name, e.g. "Mjölk"
    public double Amount { get; set; }
    public string Unit { get; set; } = "st";
}

/// <summary>Swedish cooking units and conversion to a base amount (ml / g / piece).</summary>
public static class Units
{
    public static readonly Dictionary<string, (UnitType Type, double Factor)> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["l"] = (UnitType.Volume, 1000),
            ["dl"] = (UnitType.Volume, 100),
            ["msk"] = (UnitType.Volume, 15),
            ["tsk"] = (UnitType.Volume, 5),
            ["krm"] = (UnitType.Volume, 1),
            ["kg"] = (UnitType.Mass, 1000),
            ["g"] = (UnitType.Mass, 1),
            ["st"] = (UnitType.Piece, 1),
            ["förp"] = (UnitType.Piece, 1),
            ["klyfta"] = (UnitType.Piece, 1),
            ["näve"] = (UnitType.Piece, 1),
        };

    // Pick a human-readable unit for an aggregated base amount (ports the old UnitService thresholds).
    public static (string Unit, double Amount) Display(UnitType type, double baseAmount) => type switch
    {
        UnitType.Mass => baseAmount >= 1000 ? ("kg", baseAmount / 1000) : ("g", baseAmount),
        UnitType.Volume when baseAmount >= 1000 => ("l", baseAmount / 1000),
        UnitType.Volume when baseAmount >= 100 => ("dl", baseAmount / 100),
        UnitType.Volume when baseAmount >= 15 => ("msk", baseAmount / 15),
        UnitType.Volume when baseAmount >= 5 => ("tsk", baseAmount / 5),
        UnitType.Volume => ("krm", baseAmount),
        _ => ("st", baseAmount),
    };
}

/// <summary>Turns a pile of recipe ingredients into clean, de-duped shopping lines.</summary>
public static class ShoppingAggregator
{
    public static List<string> Aggregate(IEnumerable<RecipeIngredient> ingredients)
    {
        var rows = new List<string>();
        foreach (var group in ingredients.GroupBy(i => i.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var known = Units.Table.TryGetValue(group.First().Unit, out var first);
            // Only fold together when every entry shares the same measurement type.
            if (known && group.All(i => Units.Table.TryGetValue(i.Unit, out var t) && t.Type == first.Type))
            {
                var baseSum = group.Sum(i => i.Amount * Units.Table[i.Unit].Factor);
                var (unit, amount) = Units.Display(first.Type, baseSum);
                rows.Add(Format(amount, unit, group.Key));
            }
            else
            {
                // Mixed or unknown units: sum per exact unit so nothing is silently lost.
                foreach (var byUnit in group.GroupBy(i => i.Unit, StringComparer.OrdinalIgnoreCase))
                    rows.Add(Format(byUnit.Sum(i => i.Amount), byUnit.Key, group.Key));
            }
        }
        return rows;
    }

    public static string Format(double amount, string unit, string name)
    {
        var a = Math.Round(amount, 2);
        if (a <= 0) return name; // e.g. "salt efter smak" — no meaningful quantity
        var num = a % 1 == 0 ? ((long)a).ToString() : a.ToString("0.##", CultureInfo.InvariantCulture);
        return string.Equals(unit, "st", StringComparison.OrdinalIgnoreCase)
            ? $"{num} {name}"
            : $"{num} {unit} {name}";
    }
}
