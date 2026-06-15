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
    else { meal.Title = input.Title; meal.RecipeId = input.RecipeId; }
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

// ---- ICA: push the upcoming week's ingredients + open shopping items as one self-scan list ----
app.MapPost("/api/ica/push", async (AppDb db, IcaService ica) =>
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var weekEnd = today.AddDays(7);

    var plannedRecipeIds = await db.Meals
        .Where(m => m.RecipeId != null && m.Date >= today && m.Date < weekEnd)
        .Select(m => m.RecipeId!.Value)
        .ToListAsync();

    // A recipe planned N times contributes its ingredients N times, so quantities scale naturally.
    var ingredients = new List<RecipeIngredient>();
    if (plannedRecipeIds.Count > 0)
    {
        var recipes = await db.Recipes.Include(r => r.Ingredients)
            .Where(r => plannedRecipeIds.Contains(r.Id)).ToListAsync();
        var byId = recipes.ToDictionary(r => r.Id);
        foreach (var rid in plannedRecipeIds)
            if (byId.TryGetValue(rid, out var r)) ingredients.AddRange(r.Ingredients);
    }

    var rows = ShoppingAggregator.Aggregate(ingredients);

    // Append the open (unchecked) manual shopping items.
    var openItems = await db.ShoppingItems.Where(i => !i.Checked).OrderByDescending(i => i.CreatedAt).ToListAsync();
    rows.AddRange(openItems.Select(i => string.IsNullOrWhiteSpace(i.Qty) ? i.Name : $"{i.Qty} {i.Name}"));

    var title = $"Hemma v.{System.Globalization.ISOWeek.GetWeekOfYear(DateTime.Today)}";
    var (sent, error) = await ica.PushList(title, rows);
    return Results.Ok(new { sent, error, title, rows, count = rows.Count });
});

app.Run();

public record StatusChange(ChoreStatus Status);
