using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DocBook.Api.Tests;

[Collection(nameof(ApiCollection))]
public class BookingApiTests(DocBookApiFixture fixture)
{
    private readonly Guid _doctor = Guid.NewGuid();
    private readonly DateOnly _date = ApiFlows.Tomorrow();

    [Fact]
    public async Task A_patient_books_a_free_slot_and_reads_it_back()
    {
        var patient = await OpenDayAndSignIn();
        var slots = await ApiFlows.FreeSlotsAsync(patient, _doctor, _date);

        var appointmentId = await ApiFlows.BookedAppointmentAsync(patient, _doctor, slots[0]);

        var mine = await patient.GetFromJsonAsync<JsonElement>("/api/v1/appointments/mine");
        var item = Assert.Single(mine.GetProperty("items").EnumerateArray());

        Assert.Equal(appointmentId, item.GetProperty("appointmentId").GetGuid());
        Assert.Equal(_doctor, item.GetProperty("doctorId").GetGuid());
        Assert.Equal("Booked", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_booked_slot_stops_being_offered()
    {
        var patient = await OpenDayAndSignIn();
        var before = await ApiFlows.FreeSlotsAsync(patient, _doctor, _date);

        await ApiFlows.BookedAppointmentAsync(patient, _doctor, before[0]);

        var after = await ApiFlows.FreeSlotsAsync(patient, _doctor, _date);
        Assert.Equal(before.Count - 1, after.Count);
        Assert.DoesNotContain(after, slot => slot.Start == before[0].Start);
    }

    [Fact]
    public async Task A_day_nobody_opened_has_no_slots_to_read()
    {
        var patient = await fixture.Factory.AsNewPatientAsync(ApiFlows.NewEmail());

        var response = await patient.GetAsync(
            $"/api/v1/doctors/{Guid.NewGuid()}/days/{_date:yyyy-MM-dd}/free-slots?minutes=30");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_patient_may_not_open_a_doctors_day()
    {
        var patient = await fixture.Factory.AsNewPatientAsync(ApiFlows.NewEmail());

        var response = await patient.PostAsJsonAsync($"/api/v1/doctors/{_doctor}/days", new
        {
            date = _date.ToString("yyyy-MM-dd"),
            opensAt = "09:00:00",
            closesAt = "17:00:00"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Opening_a_day_that_is_already_open_is_a_conflict()
    {
        var staff = await fixture.Factory.AsStaffAsync();
        await ApiFlows.OpenDayAsync(staff, _doctor, _date);

        var response = await staff.PostAsJsonAsync($"/api/v1/doctors/{_doctor}/days", new
        {
            date = _date.ToString("yyyy-MM-dd"),
            opensAt = "09:00:00",
            closesAt = "17:00:00"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_patient_may_not_book_for_someone_else()
    {
        var patient = await OpenDayAndSignIn();
        var slots = await ApiFlows.FreeSlotsAsync(patient, _doctor, _date);

        var response = await ApiFlows.BookAsync(patient, _doctor, slots[0], Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Closed hours and a taken slot answer identically, so a refusal never says the doctor is busy.
    [Fact]
    public async Task A_closed_hour_and_a_taken_slot_are_refused_in_the_same_words()
    {
        var patient = await OpenDayAndSignIn();
        var slots = await ApiFlows.FreeSlotsAsync(patient, _doctor, _date);
        await ApiFlows.BookedAppointmentAsync(patient, _doctor, slots[0]);

        var closed = new Slot(
            new DateTimeOffset(_date.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero),
            new DateTimeOffset(_date.ToDateTime(new TimeOnly(8, 30)), TimeSpan.Zero));

        var outsideHours = await ApiFlows.BookAsync(patient, _doctor, closed);
        var alreadyTaken = await ApiFlows.BookAsync(patient, _doctor, slots[0]);

        Assert.Equal(HttpStatusCode.Conflict, outsideHours.StatusCode);
        Assert.Equal(outsideHours.StatusCode, alreadyTaken.StatusCode);
        Assert.Equal(
            await Title(outsideHours),
            await Title(alreadyTaken));
    }

    // Someone else's appointment must read exactly as one that never existed, or ids can be tested.
    [Fact]
    public async Task Cancelling_someone_elses_appointment_answers_as_one_that_does_not_exist()
    {
        var owner = await OpenDayAndSignIn();
        var slots = await ApiFlows.FreeSlotsAsync(owner, _doctor, _date);
        var appointmentId = await ApiFlows.BookedAppointmentAsync(owner, _doctor, slots[0]);

        var stranger = await fixture.Factory.AsNewPatientAsync(ApiFlows.NewEmail());

        var someoneElses = await stranger.PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/cancellation",
            new { reason = "not mine to cancel" });

        var neverExisted = await stranger.PostAsJsonAsync(
            $"/api/v1/appointments/{Guid.NewGuid()}/cancellation",
            new { reason = "no such appointment" });

        Assert.Equal(HttpStatusCode.NotFound, someoneElses.StatusCode);
        Assert.Equal(neverExisted.StatusCode, someoneElses.StatusCode);
    }

    [Fact]
    public async Task A_patient_cancels_their_own_appointment_and_the_slot_comes_back()
    {
        var patient = await OpenDayAndSignIn();
        var slots = await ApiFlows.FreeSlotsAsync(patient, _doctor, _date);
        var appointmentId = await ApiFlows.BookedAppointmentAsync(patient, _doctor, slots[0]);

        var response = await patient.PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/cancellation",
            new { reason = "going on holiday" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(
            await ApiFlows.FreeSlotsAsync(patient, _doctor, _date),
            slot => slot.Start == slots[0].Start);
    }

    // The aggregate cannot stop two requests that each loaded it; the concurrency token can.
    [Fact]
    public async Task Two_patients_racing_for_one_slot_leave_one_appointment()
    {
        var staff = await fixture.Factory.AsStaffAsync();
        await ApiFlows.OpenDayAsync(staff, _doctor, _date);

        var first = await fixture.Factory.AsNewPatientAsync(ApiFlows.NewEmail());
        var second = await fixture.Factory.AsNewPatientAsync(ApiFlows.NewEmail());
        var slot = (await ApiFlows.FreeSlotsAsync(first, _doctor, _date))[0];

        var responses = await Task.WhenAll(
            ApiFlows.BookAsync(first, _doctor, slot),
            ApiFlows.BookAsync(second, _doctor, slot));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_page_size_past_the_cap_is_refused()
    {
        var patient = await fixture.Factory.AsNewPatientAsync(ApiFlows.NewEmail());

        var response = await patient.GetAsync("/api/v1/appointments/mine?page=1&size=5000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_slot_length_past_the_cap_is_refused()
    {
        var patient = await OpenDayAndSignIn();

        var response = await patient.GetAsync(
            $"/api/v1/doctors/{_doctor}/days/{_date:yyyy-MM-dd}/free-slots?minutes=1440");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_reason_past_the_length_limit_is_refused_before_a_handler_runs()
    {
        var patient = await OpenDayAndSignIn();
        var slots = await ApiFlows.FreeSlotsAsync(patient, _doctor, _date);

        var response = await patient.PostAsJsonAsync("/api/v1/appointments", new
        {
            doctorId = _doctor,
            start = slots[0].Start,
            end = slots[0].End,
            reason = new string('x', 5000)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpClient> OpenDayAndSignIn()
    {
        var staff = await fixture.Factory.AsStaffAsync();
        await ApiFlows.OpenDayAsync(staff, _doctor, _date);

        return await fixture.Factory.AsNewPatientAsync(ApiFlows.NewEmail());
    }

    private static async Task<string?> Title(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("title").GetString();
    }
}
