using System.Text.Json.Serialization;
using Household.Api;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDb>(o =>
    o.UseSqlite($"Data Source={Path.Combine(builder.Environment.ContentRootPath, "household.db")}"));

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- Bootstrap: everything the app needs in one round-trip ----
app.MapGet("/api/bootstrap", async (AppDb db) =>
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
            .OrderBy(m => m.Date).ToListAsync()
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
        next = new Chore
        {
            Title = chore.Title,
            Notes = chore.Notes,
            Category = chore.Category,
            Priority = chore.Priority,
            AssigneeId = chore.AssigneeId,
            DueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(days),
            RecurDays = days,
        };
        chore.RecurDays = null; // the new occurrence carries the recurrence forward
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
    else meal.Title = input.Title;
    await db.SaveChangesAsync();
    return Results.Ok(meal);
});

app.Run();

public record StatusChange(ChoreStatus Status);
