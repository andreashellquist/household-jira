using Household.Api;
using Xunit;

namespace Household.Api.Tests;

public class IngredientParsingTests
{
    [Theory]
    [InlineData("3 dl vetemjöl", 3, "dl", "vetemjöl")]
    [InlineData("2 ägg", 2, "st", "ägg")]
    [InlineData("1 msk socker", 1, "msk", "socker")]
    [InlineData("100 g smör", 100, "g", "smör")]
    [InlineData("1/2 tsk salt", 0.5, "tsk", "salt")]
    [InlineData("1 1/2 dl grädde", 1.5, "dl", "grädde")]
    [InlineData("½ dl olja", 0.5, "dl", "olja")]
    [InlineData("2,5 dl mjölk", 2.5, "dl", "mjölk")]
    [InlineData("salt efter smak", 0, "st", "salt efter smak")]
    [InlineData("1 kruka basilika", 1, "st", "kruka basilika")] // unknown unit stays in name
    public void ParsesQuantityUnitAndName(string raw, double amount, string unit, string name)
    {
        var ing = RecipeImporter.ParseIngredient(raw);
        Assert.Equal(amount, ing.Amount, 3);
        Assert.Equal(unit, ing.Unit);
        Assert.Equal(name, ing.Name);
    }
}

public class AggregationTests
{
    [Fact]
    public void SumsSameProductAcrossUnits()
    {
        var ingredients = new List<RecipeIngredient>
        {
            new() { Name = "Mjölk", Amount = 3, Unit = "dl" },
            new() { Name = "Mjölk", Amount = 2, Unit = "dl" },
            new() { Name = "Ägg", Amount = 3, Unit = "st" },
            new() { Name = "Smör", Amount = 0, Unit = "st" }, // qty-less
        };

        var rows = ShoppingAggregator.Aggregate(ingredients);

        Assert.Contains("5 dl Mjölk", rows);
        Assert.Contains("3 Ägg", rows);
        Assert.Contains("Smör", rows); // no "0" prefix
    }

    [Fact]
    public void PromotesToLargerUnit()
    {
        var rows = ShoppingAggregator.Aggregate(new List<RecipeIngredient>
        {
            new() { Name = "Mjölk", Amount = 6, Unit = "dl" },
            new() { Name = "Mjölk", Amount = 5, Unit = "dl" }, // 1100 ml -> 1.1 l
        });
        Assert.Contains("1.1 l Mjölk", rows);
    }
}

public class HtmlExtractionTests
{
    // schema.org Recipe inside an @graph, with HowToStep instructions — the common shape.
    private const string Html = """
        <html><head>
        <script type="application/ld+json">
        {
          "@context": "https://schema.org",
          "@graph": [
            { "@type": "WebPage", "name": "ignore me" },
            {
              "@type": ["Recipe"],
              "name": "Klassiska pannkakor",
              "recipeYield": "4 portioner",
              "totalTime": "PT1H30M",
              "image": { "@type": "ImageObject", "url": "https://example.com/p.jpg" },
              "recipeIngredient": ["3 dl vetemjöl", "5 dl mjölk", "2 ägg", "1 krm salt"],
              "recipeInstructions": [
                { "@type": "HowToStep", "text": "Vispa ihop mjöl och mjölk." },
                { "@type": "HowToStep", "text": "Stek i smör." }
              ]
            }
          ]
        }
        </script>
        </head><body></body></html>
        """;

    [Fact]
    public void ExtractsRecipeFromJsonLd()
    {
        var recipe = RecipeImporter.ExtractFromHtml(Html);

        Assert.NotNull(recipe);
        Assert.Equal("Klassiska pannkakor", recipe!.Name);
        Assert.Equal(4, recipe.Ingredients.Count);
        Assert.Equal("Vispa ihop mjöl och mjölk.\nStek i smör.", recipe.Instructions);

        var flour = recipe.Ingredients[0];
        Assert.Equal("vetemjöl", flour.Name);
        Assert.Equal(3, flour.Amount, 3);
        Assert.Equal("dl", flour.Unit);

        Assert.Equal(4, recipe.Servings);
        Assert.Equal(90, recipe.CookMinutes); // 1h30m
        Assert.Equal("https://example.com/p.jpg", recipe.ImageUrl);
    }

    [Fact]
    public void ReturnsNullWhenNoRecipe()
    {
        Assert.Null(RecipeImporter.ExtractFromHtml("<html><body>no structured data</body></html>"));
    }
}
