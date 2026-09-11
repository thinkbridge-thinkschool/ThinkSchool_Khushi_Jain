using DocBook.SharedKernel;

namespace DocBook.Infrastructure;

// Only registered types come back out, so a tampered row cannot name any type it likes.
public sealed class IntegrationEventTypeMap
{
    private readonly Dictionary<string, Type> _byName = [];

    public string NameOf(IIntegrationEvent integrationEvent) => integrationEvent.GetType().Name;

    public void Register<TEvent>() where TEvent : IIntegrationEvent =>
        _byName[typeof(TEvent).Name] = typeof(TEvent);

    public Type? Find(string name) => _byName.GetValueOrDefault(name);
}
