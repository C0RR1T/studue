using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Studue.Services;
using WebPush;

namespace Studue;

public class PushSubscriptionDto
{
    public required string Endpoint { get; set; }
    public required PushSubscriptionKeysDto Keys { get; set; }
}

public class PushSubscriptionKeysDto
{
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
}

public class PushUnsubscribeDto
{
    public required string Endpoint { get; set; }
}

public class PushService(IDbContextFactory<DatabaseContext> contextFactory, IOptions<Settings> settings, ILogger<PushService> logger) : BackgroundService
{
    public static void RegisterEndpoint(WebApplication webApplication)
    {
        webApplication.MapPost("/push/subscribe", async ([FromBody] PushSubscriptionDto subscriptionDto, DatabaseContext databaseContext, StudentContext studentContext) =>
        {
            var existing = await databaseContext.PushSubscriptions
                .FirstOrDefaultAsync(x => x.Endpoint == subscriptionDto.Endpoint);

            if (existing == null)
            {
                databaseContext.PushSubscriptions.Add(new PushSubscriptionRow
                {
                    Auth = subscriptionDto.Keys.Auth,
                    Endpoint = subscriptionDto.Endpoint,
                    P256DH = subscriptionDto.Keys.P256dh,
                    Student = studentContext.Student
                });
            }
            else
            {
                existing.Auth = subscriptionDto.Keys.Auth;
                existing.P256DH = subscriptionDto.Keys.P256dh;
                existing.Student = studentContext.Student;
            }

            await databaseContext.SaveChangesAsync();

            return Results.Ok();
        }).WithMetadata(new StudentRequiredAttribute());

        webApplication.MapPost("/push/unsubscribe", async ([FromBody] PushUnsubscribeDto unsubscribeDto, DatabaseContext databaseContext, StudentContext studentContext) =>
        {
            await databaseContext.PushSubscriptions
                .Where(x => x.Endpoint == unsubscribeDto.Endpoint && x.Student == studentContext.Student)
                .ExecuteDeleteAsync();

            return Results.Ok();
        }).WithMetadata(new StudentRequiredAttribute());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await using var studueContext = await contextFactory.CreateDbContextAsync(stoppingToken);

            try
            {
                await SendNotifications(studueContext);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while sending notifications");
            }
            finally
            {
                await studueContext.SaveChangesAsync(stoppingToken);
            }
        }
    }

    private async Task SendNotifications(DatabaseContext databaseContext)
    {
        var lastNotificationTimeConfig = await databaseContext.Configs.FirstAsync(x => x.Id == "LastNotificationTime");
        var lastNotificationTime = DateTime.Parse(lastNotificationTimeConfig.Data, CultureInfo.InvariantCulture);

        var now = Helper.Now();
        lastNotificationTimeConfig.Data = now.ToString(CultureInfo.InvariantCulture);

        var futureAssignments = await databaseContext.Assignements
            .Include(x => x.ModuleInstance)
            .ThenInclude(x => x.Module)
            .Where(x => x.DueDateTime > now && !x.IsDeleted)
            .ToListAsync();

        (TimeSpan timespan, string dueInString)[] relevantTimes = [(TimeSpan.FromHours(1), "1 hour"), (TimeSpan.FromDays(1), "1 day")];

        foreach (var relevantTime in relevantTimes)
        {
            var assignmentsToNotify = futureAssignments.Where(x =>
            {
                var timeWhenNotificationShouldBeSent = x.DueDateTime - relevantTime.timespan;
                return timeWhenNotificationShouldBeSent > lastNotificationTime && timeWhenNotificationShouldBeSent < now;
            }).ToList();

            foreach (var assignment in assignmentsToNotify)
            {
                await SendAsync(databaseContext, assignment, relevantTime.dueInString);
            }
        }
    }

    private async Task SendAsync(DatabaseContext databaseContext, Assignment assignment, string dueIn)
    {
        var subscriptions = await databaseContext.Students
            .Where(x => x.ModuleInstances.Any(y => y.Assignements.Contains(assignment)) && !x.CompletedAssignments.Contains(assignment))
            .SelectMany(x => x.PushSubscriptions)
            .ToListAsync();

        logger.LogInformation("Sending {0} notifications for '{1}' assignment", subscriptions.Count, assignment.Title);

        var publicKey = await databaseContext.Configs.FirstAsync(x => x.Id == "VapidKey.Public");
        var privateKey = await databaseContext.Configs.FirstAsync(x => x.Id == "VapidKey.Private");

        var vapidDetails = new VapidDetails(settings.Value.FrontendUrl, publicKey.Data, privateKey.Data);

        var client = new WebPushClient();

        var payload = JsonSerializer.Serialize(new
        {
            title = $"{assignment.Title} is due in {dueIn}",
            body = assignment.ModuleInstance.Module.Name,
            url = new Uri(new Uri(settings.Value.FrontendUrl), assignment.Id.ToString()).ToString()
        });

        foreach (var batch in subscriptions.Chunk(10))
        {
            var tasks = batch.Select(async sub =>
            {
                var subscription = new PushSubscription(
                    sub.Endpoint,
                    sub.P256DH,
                    sub.Auth
                );

                try
                {
                    await client.SendNotificationAsync(subscription, payload, vapidDetails);
                }
                catch (WebPushException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
                {
                    logger.LogInformation("Dropping expired push subscription #{SubscriptionId} ({StatusCode})", sub.Id, ex.StatusCode);
                    databaseContext.PushSubscriptions.Remove(sub);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Push to subscription #{SubscriptionId} failed", sub.Id);
                }
            });
            await Task.WhenAll(tasks);
        }
    }
}