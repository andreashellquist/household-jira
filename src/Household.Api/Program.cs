using System.Text.Json.Serialization;
using Household.Api;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Local-only secrets (ICA login etc.) — gitignored, optional.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddDbContext<AppDb>(o =>
    o.UseSqlite($"Data Source={Path.Combine(builder.Environment.ContentRootPath, "household.db")}"));

builder.Services.AddHttpClient();
builder.Services.AddScoped<IcaService>();
builder.Services.AddSingleton<PushService>();
builder.Services.AddHostedService<DueChoreNotifier>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- Bootstrap: everything the app needs in one round-trip ----
app.MapGet("/api/bootstrap", async (AppDb db, IcaService scopedIca) =>
{
    var doneCutoff = DateTime.UtcNow.AddDays(-7);
    return new
    {
        Members = await db.Members.OrderBy(m => m.Name).ToListAsync(),
        Chores = await db.Chores
            .Where(c => c.Status != ChoreStatus.Done || c.CompletedAt > doneCutoff)
            .OrderBy(c => c.DueDate == null).ThenBy(c => c.DueDate).ThenByDescending(c => c.Priority)
            .ToListAsync(),
        Shopping = await db.ShoppingItems.OrderBy(i => i.Checked).ThenByDescending(i => i.CreatedAt).ToListAsync(),
        Meals = await db.Meals
            .Where(m => m.Date >= DateOnly.FromDateTime(DateTime.Today.AddDays(-7)))
            .OrderBy(m => m.Date).ToListAsync(),
        Recipes = await db.Recipes.Include(r => r.Ingredients).OrderBy(r => r.Name).ToListAsync(),
        Staples = await db.Staples.OrderByDescending(s => s.Count).ThenByDescending(s => s.LastUsed).Take(8).ToListAsync(),
        IcaConfigured = scopedIca.IsConfigured,
    };
});

// ---- Members ----
app.MapPost("/api/members", async (AppDb db, Member member) =>
{
    db.Members.Add(member);
    await db.SaveChangesAsync();
    return Results.Created($"/api/members/{member.Id}", member);
});

app.MapDelete("/api/members/{id:int}", async (AppDb db, int id) =>
{
    var deleted = await db.Members.Where(m => m.Id == id).ExecuteDeleteAsync();
    return deleted > 0 ? Results.NoContent() : Results.NotFound();
});

// ---- Chores ----
app.MapPost("/api/chores", async (AppDb db, Chore chore) =>
{
    db.Chores.Add(chore);
    await db.SaveChangesAsync();
    return Results.Created($"/api/chores/{chore.Id}", chore);
});

app.MapPut("/api/chores/{id:int}", async (AppDb db, int id, Chore input) =>
{
    var chore = await db.Chores.FindAsync(id);
    if (chore is null) return Results.NotFound();
    chore.Title = input.Title;
    chore.Notes = input.Notes;
    chore.Category = input.Category;
    chore.Priority = input.Priority;
    chore.AssigneeId = input.AssigneeId;
    chore.DueDate = input.DueDate;
    chore.RecurDays = input.RecurDays;
    chore.Rotate = input.Rotate;
    chore.RemindDaysBefore = input.RemindDaysBefore;
    await db.SaveChangesAsync();
    return Results.Ok(chore);
});

// Status change; completing a recurring chore spawns the next occurrence.
app.MapPost("/api/chores/{id:int}/status", async (AppDb db, int id, StatusChange change) =>
{
    var chore = await db.Chores.FindAsync(id);
    if (chore is null) return Results.NotFound();

    chore.Status = change.Status;
    chore.CompletedAt = change.Status == ChoreStatus.Done ? DateTime.UtcNow : null;

    Chore? next = null;
    if (change.Status == ChoreStatus.Done && chore.RecurDays is int days)
    {
        var assigneeId = chore.AssigneeId;
        if (chore.Rotate)
        {
            // Hand the next occurrence to the next family member (by id order, wrapping around).
            var memberIds = await db.Members.OrderBy(m => m.Id).Select(m => m.Id).ToListAsync();
            if (memberIds.Count > 0)
            {
                var idx = chore.AssigneeId is int cur ? memberIds.IndexOf(cur) : -1;
                assigneeId = memberIds[(idx + 1) % memberIds.Count];
            }
        }
        next = new Chore
        {
            Title = chore.Title,
            Notes = chore.Notes,
            Category = chore.Category,
            Priority = chore.Priority,
            AssigneeId = assigneeId,
            DueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(days),
            RecurDays = days,
            Rotate = chore.Rotate,
            RemindDaysBefore = chore.RemindDaysBefore,
        };
        chore.RecurDays = null; // the new occurrence carries the recurrence forward
        chore.Rotate = false;
        db.Chores.Add(next);
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { chore, next });
});

app.MapDelete("/api/chores/{id:int}", async (AppDb db, int id) =>
{
    var deleted = await db.Chores.Where(c => c.Id == id).ExecuteDeleteAsync();
    return deleted > 0 ? Results.NoContent() : Results.NotFound();
});

// ---- Shopping list ----
app.MapPost("/api/shopping", async (AppDb db, ShoppingItem item) =>
{
    db.ShoppingItems.Add(item);

    // Learn this item as a staple (for quick-add chips), keyed case-insensitively by name.
    var name = item.Name.Trim();
    var staple = await db.Staples.FirstOrDefaultAsync(s => s.Name.ToLower() == name.ToLower());
    if (staple is null)
        db.Staples.Add(new Staple { Name = name, Qty = item.Qty, Count = 1 });
    else
    {
        staple.Count++;
        staple.LastUsed = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(item.Qty)) staple.Qty = item.Qty;
    }

    await db.SaveChangesAsync();
    return Results.Created($"/api/shopping/{item.Id}", item);
});

app.MapPost("/api/shopping/{id:int}/toggle", async (AppDb db, int id) =>
{
    var item = await db.ShoppingItems.FindAsync(id);
    if (item is null) return Results.NotFound();
    item.Checked = !item.Checked;
    await db.SaveChangesAsync();
    return Results.Ok(item);
});

app.MapPost("/api/shopping/clear-checked", async (AppDb db) =>
{
    await db.ShoppingItems.Where(i => i.Checked).ExecuteDeleteAsync();
    return Results.NoContent();
});

app.MapDelete("/api/shopping/{id:int}", async (AppDb db, int id) =>
{
    var deleted = await db.ShoppingItems.Where(i => i.Id == id).ExecuteDeleteAsync();
    return deleted > 0 ? Results.NoContent() : Results.NotFound();
});

// ---- Meal plan (upsert by date+slot; empty title clears the slot) ----
app.MapPut("/api/meals", async (AppDb db, Meal input) =>
{
    var meal = await db.Meals.FirstOrDefaultAsync(m => m.Date == input.Date && m.Slot == input.Slot);
    if (string.IsNullOrWhiteSpace(input.Title))
    {
        if (meal is not null) { db.Meals.Remove(meal); await db.SaveChangesAsync(); }
        return Results.NoContent();
    }
    if (meal is null) { meal = input; db.Meals.Add(meal); }
    else { meal.Title = input.Title; meal.RecipeId = input.RecipeId; meal.Servings = input.Servings; meal.Kind = input.Kind; }
    await db.SaveChangesAsync();
    return Results.Ok(meal);
});

// ---- Recipes ----
app.MapPost("/api/recipes", async (AppDb db, Recipe recipe) =>
{
    db.Recipes.Add(recipe);
    await db.SaveChangesAsync();
    return Results.Created($"/api/recipes/{recipe.Id}", recipe);
});

app.MapPut("/api/recipes/{id:int}", async (AppDb db, int id, Recipe input) =>
{
    var recipe = await db.Recipes.Include(r => r.Ingredients).FirstOrDefaultAsync(r => r.Id == id);
    if (recipe is null) return Results.NotFound();
    recipe.Name = input.Name;
    recipe.Source = input.Source;
    recipe.Instructions = input.Instructions;
    recipe.Preparations = input.Preparations;
    recipe.Servings = input.Servings;
    recipe.CookMinutes = input.CookMinutes;
    recipe.ImageUrl = input.ImageUrl;
    recipe.Ingredients.Clear(); // simplest correct path: replace the ingredient set wholesale
    recipe.Ingredients.AddRange(input.Ingredients);
    await db.SaveChangesAsync();
    return Results.Ok(recipe);
});

app.MapDelete("/api/recipes/{id:int}", async (AppDb db, int id) =>
{
    var deleted = await db.Recipes.Where(r => r.Id == id).ExecuteDeleteAsync();
    return deleted > 0 ? Results.NoContent() : Results.NotFound();
});

// Import a recipe from a URL by reading its schema.org Recipe JSON-LD.
app.MapPost("/api/recipes/import", async (ImportRequest req, IHttpClientFactory factory) =>
{
    if (string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "Ange en länk." });
    if (!RecipeImporter.IsAllowed(req.Url, out var error))
        return Results.BadRequest(new { error });
    var http = factory.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(15);
    try
    {
        var recipe = await RecipeImporter.Import(req.Url, http);
        return recipe is null
            ? Results.Ok(new { found = false })
            : Results.Ok(new { found = true, recipe });
    }
    catch
    {
        return Results.Ok(new { found = false });
    }
});

// ---- ICA self-scan list ----
// Preview the assembled week (scaled recipe ingredients + open shopping items) for review/edit.
app.MapGet("/api/ica/preview", async (AppDb db, IcaService ica) =>
{
    var (title, rows) = await IcaListBuilder.BuildWeek(db);
    return Results.Ok(new { title, rows, configured = ica.IsConfigured });
});

// Push the (possibly edited) list to ICA.
app.MapPost("/api/ica/push", async (IcaService ica, PushRequest req) =>
{
    var title = string.IsNullOrWhiteSpace(req.Title)
        ? $"Hemma v.{System.Globalization.ISOWeek.GetWeekOfYear(DateTime.Today)}"
        : req.Title.Trim();
    var rows = (req.Rows ?? new List<string>())
        .Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
    var (sent, error) = await ica.PushList(title, rows);
    return Results.Ok(new { sent, error, title, count = rows.Count });
});

// ---- Push notifications ----
app.MapGet("/api/push/key", (PushService push) =>
    Results.Ok(new { publicKey = push.PublicKey, enabled = push.Enabled }));

app.MapPost("/api/push/subscribe", async (AppDb db, SubscribeRequest req) =>
{
    var existing = await db.PushSubs.FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint);
    if (existing is null)
        db.PushSubs.Add(new PushSub { Endpoint = req.Endpoint, P256dh = req.P256dh, Auth = req.Auth, MemberId = req.MemberId });
    else
    {
        existing.P256dh = req.P256dh;
        existing.Auth = req.Auth;
        existing.MemberId = req.MemberId;
        existing.LastNotified = null;
    }
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapPost("/api/push/test", async (AppDb db, PushService push, TestRequest req) =>
{
    var subs = req.MemberId is int mid
        ? await db.PushSubs.Where(s => s.MemberId == mid).ToListAsync()
        : await db.PushSubs.ToListAsync();
    var sent = 0;
    foreach (var s in subs)
    {
        var r = await push.Send(s, new { title = "Hemma", body = "Påminnelser fungerar! 🔔", url = "/" });
        if (r == PushResult.Gone) db.PushSubs.Remove(s);
        else if (r == PushResult.Ok) sent++;
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { sent, enabled = push.Enabled });
});

app.Run();

public record StatusChange(ChoreStatus Status);
