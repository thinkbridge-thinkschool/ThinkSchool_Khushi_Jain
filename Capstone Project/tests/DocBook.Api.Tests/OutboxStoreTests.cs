using DocBook.Infrastructure;
using DocBook.Notifications.Infrastructure;
using DocBook.Patients.Infrastructure;
using DocBook.Scheduling.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocBook.Api.Tests;

// The claim is raw SQL with a table hint, so real SQL Server is the only place it can be tested.
[Collection(nameof(ApiCollection))]
public class OutboxStoreTests(DocBookApiFixture fixture)
{
    private const int WholeTable = 1000;

    [Fact]
    public async Task Every_module_has_its_migrations_applied()
    {
        using var scope = fixture.Factory.Services.CreateScope();

        Assert.NotEmpty(await scope.ServiceProvider.GetRequiredService<PatientsDbContext>()
            .Database.GetAppliedMigrationsAsync());

        Assert.NotEmpty(await scope.ServiceProvider.GetRequiredService<SchedulingDbContext>()
            .Database.GetAppliedMigrationsAsync());

        Assert.NotEmpty(await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>()
            .Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task A_pending_message_is_claimed()
    {
        var id = await WriteMessage();

        var claimed = await Claim(TimeSpan.FromMinutes(5));

        Assert.Contains(id, claimed);
    }

    // Without the claim two instances polling together would both deliver the same batch.
    [Fact]
    public async Task A_message_another_claim_holds_is_not_claimed_again()
    {
        var id = await WriteMessage();
        await Claim(TimeSpan.FromMinutes(5));

        var second = await Claim(TimeSpan.FromMinutes(5));

        Assert.DoesNotContain(id, second);
    }

    // A lease rather than a lock, so an instance that dies mid-delivery strands nothing.
    [Fact]
    public async Task A_claim_that_has_run_out_is_claimed_again()
    {
        var id = await WriteMessage();
        await Claim(TimeSpan.FromSeconds(-1));

        var second = await Claim(TimeSpan.FromMinutes(5));

        Assert.Contains(id, second);
    }

    [Fact]
    public async Task A_message_already_delivered_is_never_claimed()
    {
        var id = await WriteMessage(message => message.ProcessedAt = DateTimeOffset.UtcNow);

        Assert.DoesNotContain(id, await Claim(TimeSpan.FromMinutes(5)));
    }

    // Abandoned is its own stamp, so a message that never sent stops being retried and stays findable.
    [Fact]
    public async Task An_abandoned_message_is_never_claimed()
    {
        var id = await WriteMessage(message => message.AbandonedAt = DateTimeOffset.UtcNow);

        Assert.DoesNotContain(id, await Claim(TimeSpan.FromMinutes(5)));
    }

    private async Task<Guid> WriteMessage(Action<OutboxMessage>? stamp = null)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();

        var message = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = "OutboxStoreTests",
            Payload = "{}",
            OccurredAt = DateTimeOffset.UtcNow
        };

        stamp?.Invoke(message);

        context.Outbox.Add(message);
        await context.SaveChangesAsync();

        return message.Id;
    }

    // Its own scope each time, so a claim reads the table rather than what a previous one tracked.
    private async Task<IReadOnlyList<Guid>> Claim(TimeSpan duration)
    {
        using var scope = fixture.Factory.Services.CreateScope();

        var claimed = await scope.ServiceProvider
            .GetRequiredService<IOutboxStore>()
            .ClaimPendingAsync(WholeTable, duration, CancellationToken.None);

        return claimed.Select(message => message.Id).ToList();
    }
}
