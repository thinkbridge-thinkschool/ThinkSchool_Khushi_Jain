using DocBook.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace DocBook.Infrastructure;

// Loops rather than iterates once, because a handler is free to raise further events.
public sealed class DomainEventDispatcher(IServiceProvider services)
{
    public async Task DispatchAsync(
        IEnumerable<IHasDomainEvents> aggregates,
        CancellationToken cancellationToken)
    {
        var tracked = aggregates.ToList();

        while (true)
        {
            var pending = tracked
                .SelectMany(aggregate => aggregate.DomainEvents)
                .ToList();

            if (pending.Count == 0)
            {
                return;
            }

            foreach (var aggregate in tracked)
            {
                aggregate.ClearDomainEvents();
            }

            foreach (var domainEvent in pending)
            {
                var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());

                foreach (var handler in services.GetServices(handlerType))
                {
                    await (Task)handlerType
                        .GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!
                        .Invoke(handler, [domainEvent, cancellationToken])!;
                }
            }
        }
    }
}
