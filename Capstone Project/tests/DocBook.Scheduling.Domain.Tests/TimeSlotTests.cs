using DocBook.SharedKernel;

namespace DocBook.Scheduling.Domain.Tests;

public class TimeSlotTests
{
    private static readonly DateTimeOffset Nine = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_slot_must_end_after_it_starts()
    {
        var error = Assert.Throws<DomainException>(() => new TimeSlot(Nine, Nine.AddMinutes(-30)));

        Assert.Equal("slot_ends_before_it_starts", error.Code);
    }

    [Fact]
    public void A_slot_of_no_length_is_not_a_slot()
    {
        Assert.Throws<DomainException>(() => new TimeSlot(Nine, Nine));
    }

    // Back to back is the common case, and counting it as an overlap would halve the day.
    [Fact]
    public void Slots_that_only_touch_do_not_overlap()
    {
        var first = new TimeSlot(Nine, Nine.AddMinutes(30));
        var second = new TimeSlot(Nine.AddMinutes(30), Nine.AddMinutes(60));

        Assert.False(first.Overlaps(second));
        Assert.False(second.Overlaps(first));
    }

    [Fact]
    public void A_slot_inside_another_overlaps_it_both_ways_round()
    {
        var outer = new TimeSlot(Nine, Nine.AddMinutes(60));
        var inner = new TimeSlot(Nine.AddMinutes(15), Nine.AddMinutes(30));

        Assert.True(outer.Overlaps(inner));
        Assert.True(inner.Overlaps(outer));
    }

    [Fact]
    public void Slots_that_run_into_each_other_overlap()
    {
        var first = new TimeSlot(Nine, Nine.AddMinutes(30));
        var second = new TimeSlot(Nine.AddMinutes(15), Nine.AddMinutes(45));

        Assert.True(first.Overlaps(second));
        Assert.True(second.Overlaps(first));
    }

    [Fact]
    public void Slots_that_do_not_meet_do_not_overlap()
    {
        var morning = new TimeSlot(Nine, Nine.AddMinutes(30));
        var afternoon = new TimeSlot(Nine.AddHours(6), Nine.AddHours(6).AddMinutes(30));

        Assert.False(morning.Overlaps(afternoon));
        Assert.False(afternoon.Overlaps(morning));
    }
}
