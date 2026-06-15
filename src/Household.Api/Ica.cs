using System.Text;
using System.Text.Json;

namespace Household.Api;

public class IcaOptions
{
    public string Personnummer { get; set; } = "";
    public string Pin { get; set; } = "";
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
