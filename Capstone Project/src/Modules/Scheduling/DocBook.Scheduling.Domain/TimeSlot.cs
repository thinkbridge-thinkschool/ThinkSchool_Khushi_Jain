using DocBook.SharedKernel;

namespace DocBook.Scheduling.Domain;

public readonly record struct TimeSlot
{
    public TimeSlot(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new DomainException("An appointment must end after it starts.");
        }

        Start = start;
        End = end;
    }

    public DateTimeOffset Start { get; init; }

    public DateTimeOffset End { get; init; }

    public bool Overlaps(TimeSlot other) => Start < other.End && other.Start < End;
}
