using Microsoft.EntityFrameworkCore;

namespace Household.Api;

public enum ChoreStatus { Todo, Doing, Done }
public enum Priority { Low, Normal, Urgent }
public enum MealSlot { Lunch, Dinner }

public class Member
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#F59E0B";
}

public class Chore
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string? Notes { get; set; }
    public string Category { get; set; } = "other";
    public Priority Priority { get; set; } = Priority.Normal;
    public ChoreStatus Status { get; set; } = ChoreStatus.Todo;
    public int? AssigneeId { get; set; }
    public Member? Assignee { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>Days between occurrences; null = one-off. Completing a recurring chore spawns the next one.</summary>
    public int? RecurDays { get; set; }
    /// <summary>When a recurring chore is completed, hand the next occurrence to the next family member.</summary>
    public bool Rotate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class ShoppingItem
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Qty { get; set; }
    public bool Checked { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Meal
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public MealSlot Slot { get; set; } = MealSlot.Dinner;
    public required string Title { get; set; }
    public int? RecipeId { get; set; }  // links the planned meal to a recipe for shopping
    public int? Servings { get; set; }  // portions to cook this day; scales the shopping quantities
}

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Chore> Chores => Set<Chore>();
    public DbSet<ShoppingItem> ShoppingItems => Set<ShoppingItem>();
    public DbSet<Meal> Meals => Set<Meal>();
    public DbSet<Recipe> Recipes => Set<Recipe>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Meal>().HasIndex(m => new { m.Date, m.Slot }).IsUnique();
        b.Entity<Chore>()
            .HasOne(c => c.Assignee)
            .WithMany()
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<Recipe>()
            .HasMany(r => r.Ingredients)
            .WithOne()
            .HasForeignKey(i => i.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
