using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Household.Api;

public class IcaOptions
{
    public string Personnummer { get; set; } = "";
    public string Pin { get; set; } = "";
}

public record PushRequest(string? Title, List<string> Rows);

/// <summary>Assembles the upcoming week's shopping list (scaled recipe ingredients + open items).</summary>
public static class IcaListBuilder
{
    public static async Task<(string Title, List<string> Rows)> BuildWeek(AppDb db)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var weekEnd = today.AddDays(7);

        var plannedMeals = await db.Meals
            .Where(m => m.RecipeId != null && m.Date >= today && m.Date < weekEnd)
            .Select(m => new { RecipeId = m.RecipeId!.Value, m.Servings })
            .ToListAsync();

        // Each planned meal contributes its recipe's ingredients, scaled to the portions being cooked.
        var ingredients = new List<RecipeIngredient>();
        if (plannedMeals.Count > 0)
        {
            var recipeIds = plannedMeals.Select(m => m.RecipeId).Distinct().ToList();
            var byId = await db.Recipes.Include(r => r.Ingredients)
                .Where(r => recipeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
            foreach (var meal in plannedMeals)
            {
                if (!byId.TryGetValue(meal.RecipeId, out var r)) continue;
                var scale = meal.Servings is int s && s > 0 && r.Servings > 0 ? (double)s / r.Servings : 1.0;
                ingredients.AddRange(r.Ingredients.Select(i =>
                    new RecipeIngredient { Name = i.Name, Amount = i.Amount * scale, Unit = i.Unit }));
            }
        }

        var rows = ShoppingAggregator.Aggregate(ingredients);

        // Append the open (unchecked) manual shopping items.
        var openItems = await db.ShoppingItems.Where(i => !i.Checked)
            .OrderByDescending(i => i.CreatedAt).ToListAsync();
        rows.AddRange(openItems.Select(i => string.IsNullOrWhiteSpace(i.Qty) ? i.Name : $"{i.Qty} {i.Name}"));

        var title = $"Hemma v.{ISOWeek.GetWeekOfYear(DateTime.Today)}";
        return (title, rows);
    }
}

/// <summary>
/// Pushes a shopping list into the ICA app's "offline shopping lists" so it shows up for self-scan.
/// Reverse-engineered endpoints ported from the GroceryShopping project; credentials come from config
/// (appsettings.Local.json, gitignored) — never hard-coded.
/// </summary>
public class IcaService(IConfiguration config, IHttpClientFactory httpFactory)
{
    public bool IsConfigured
    {
        get
        {
            var o = Options;
            return !string.IsNullOrWhiteSpace(o.Personnummer) && !string.IsNullOrWhiteSpace(o.Pin);
        }
    }

    private IcaOptions Options => config.GetSection("Ica").Get<IcaOptions>() ?? new IcaOptions();

    public async Task<(bool Sent, string? Error)> PushList(string title, IReadOnlyList<string> rows)
    {
        var opts = Options;
        if (string.IsNullOrWhiteSpace(opts.Personnummer) || string.IsNullOrWhiteSpace(opts.Pin))
            return (false, "ICA-inloggning saknas — lägg till Ica:Personnummer och Ica:Pin i appsettings.Local.json.");
        if (rows.Count == 0)
            return (false, "Listan är tom.");

        try
        {
            var http = httpFactory.CreateClient();

            // 1) Authenticate (Basic personnummer:pin) and grab the ticket header.
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{opts.Personnummer}:{opts.Pin}"));
            var loginReq = new HttpRequestMessage(HttpMethod.Get, "https://handla.api.ica.se/api/login");
            loginReq.Headers.Add("Authorization", $"Basic {basic}");
            var loginRes = await http.SendAsync(loginReq);
            if (!loginRes.IsSuccessStatusCode)
                return (false, $"ICA-inloggning misslyckades ({(int)loginRes.StatusCode}). Kontrollera personnummer/PIN.");
            if (!loginRes.Headers.TryGetValues("AuthenticationTicket", out var tickets))
                return (false, "Fick ingen AuthenticationTicket från ICA (API:t kan ha ändrats).");
            var ticket = tickets.First();

            // 2) Create an offline shopping list with one text row per item.
            var now = DateTime.UtcNow;
            var list = new
            {
                OfflineId = Guid.NewGuid().ToString(),
                Title = title,
                CommentText = "",
                SortingStore = 0,
                LatestChange = now,
                Rows = rows.Select((r, i) => new
                {
                    RowId = i + 1,
                    ProductName = r,
                    Quantity = 0,
                    SourceId = 1,
                    IsStrikedOver = false,
                    InternalOrder = i + 1,
                    ArticleGroupId = 1,
                    ArticleGroupIdExtended = 1,
                    LatestChange = now,
                    OfflineId = Guid.NewGuid().ToString(),
                    IsSmartItem = true,
                }).ToList(),
            };

            var postReq = new HttpRequestMessage(HttpMethod.Post, "https://handla.api.ica.se/api/user/offlineshoppinglists")
            {
                Content = new StringContent(JsonSerializer.Serialize(list), Encoding.UTF8, "application/json"),
            };
            postReq.Headers.Add("AuthenticationTicket", ticket);
            var postRes = await http.SendAsync(postReq);
            return postRes.IsSuccessStatusCode
                ? (true, null)
                : (false, $"ICA avvisade listan ({(int)postRes.StatusCode}).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
