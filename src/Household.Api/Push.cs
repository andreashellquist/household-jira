using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebPush;

namespace Household.Api;

public class PushSub
{
    public int Id { get; set; }
    public int? MemberId { get; set; }            // whose device this is (for targeted reminders)
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public DateOnly? LastNotified { get; set; }   // de-dupes the daily digest
}

public enum PushResult { Ok, Gone, Failed }

public record SubscribeRequest(string Endpoint, string P256dh, string Auth, int? MemberId);
public record TestRequest(int? MemberId);

/// <summary>
/// Web Push sender. VAPID keys come from config, or are generated once and persisted to
/// vapid.json (gitignored) so there's nothing to set up for local/household use.
/// </summary>
public class PushService
{
    private readonly VapidDetails? _vapid;
    private readonly WebPushClient _client = new();

    public string? PublicKey { get; }
    public bool Enabled => _vapid is not null;

    public PushService(IConfiguration config, IWebHostEnvironment env, ILogger<PushService> log)
    {
        var subject = config["Push:Subject"];
        if (string.IsNullOrWhiteSpace(subject)) subject = "mailto:hemma@example.com";
        var pub = config["Push:PublicKey"];
        var priv = config["Push:PrivateKey"];

        if (string.IsNullOrWhiteSpace(pub) || string.IsNullOrWhiteSpace(priv))
        {
            var path = Path.Combine(env.ContentRootPath, "vapid.json");
            try
            {
                if (File.Exists(path))
                {
                    var saved = JsonSerializer.Deserialize<VapidKeys>(File.ReadAllText(path));
                    pub = saved?.PublicKey;
                    priv = saved?.PrivateKey;
                }
                if (string.IsNullOrWhiteSpace(pub) || string.IsNullOrWhiteSpace(priv))
                {
                    var keys = VapidHelper.GenerateVapidKeys();
                    pub = keys.PublicKey;
                    priv = keys.PrivateKey;
                    File.WriteAllText(path, JsonSerializer.Serialize(new VapidKeys(pub, priv)));
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not load or generate VAPID keys; push disabled.");
            }
        }

        if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(priv))
        {
            _vapid = new VapidDetails(subject, pub, priv);
            PublicKey = pub;
        }
    }

    public async Task<PushResult> Send(PushSub sub, object payload)
    {
        if (_vapid is null) return PushResult.Failed;
        try
        {
            var subscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
            await _client.SendNotificationAsync(subscription, JsonSerializer.Serialize(payload), _vapid);
            return PushResult.Ok;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            return PushResult.Gone; // subscription expired — caller should prune it
        }
        catch
        {
            return PushResult.Failed;
        }
    }

    private record VapidKeys(string PublicKey, string PrivateKey);
}

/// <summary>Sends each device a once-a-day morning digest of chores due today or overdue.</summary>
public class DueChoreNotifier(
    IServiceScopeFactory scopeFactory, PushService push, ILogger<DueChoreNotifier> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Delay(TimeSpan.FromSeconds(20), stop); // let the app finish starting
        while (!stop.IsCancellationRequested)
        {
            try { if (push.Enabled) await Tick(); }
            catch (Exception ex) { log.LogWarning(ex, "Due-chore notifier tick failed."); }
            await Delay(TimeSpan.FromMinutes(30), stop);
        }
    }

    private async Task Tick()
    {
        if (DateTime.Now.Hour < 7) return; // only nudge in the morning onwards
        var today = DateOnly.FromDateTime(DateTime.Today);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        var subs = await db.PushSubs.ToListAsync();
        if (subs.Count == 0) return;

        // A chore enters the digest once today reaches (due date − its lead time).
        var openDated = await db.Chores
            .Where(c => c.Status != ChoreStatus.Done && c.DueDate != null)
            .ToListAsync();
        var dueChores = openDated
            .Where(c => c.DueDate!.Value.AddDays(-(c.RemindDaysBefore ?? 0)) <= today)
            .ToList();
        if (dueChores.Count == 0) return;

        var dirty = false;
        foreach (var sub in subs.Where(s => s.LastNotified != today))
        {
            // Targeted: a device tied to a member sees their own + unassigned chores; otherwise everything.
            var mine = sub.MemberId is int mid
                ? dueChores.Where(c => c.AssigneeId == mid || c.AssigneeId == null).ToList()
                : dueChores;
            if (mine.Count == 0) continue;

            var body = mine.Count == 1 ? mine[0].Title : $"{mine.Count} uppgifter behöver göras idag";
            var result = await push.Send(sub, new { title = "Hemma — att göra idag", body, url = "/" });
            if (result == PushResult.Gone) { db.PushSubs.Remove(sub); dirty = true; }
            else if (result == PushResult.Ok) { sub.LastNotified = today; dirty = true; }
        }
        if (dirty) await db.SaveChangesAsync();
    }

    private static async Task Delay(TimeSpan span, CancellationToken stop)
    {
        try { await Task.Delay(span, stop); } catch (TaskCanceledException) { }
    }
}
