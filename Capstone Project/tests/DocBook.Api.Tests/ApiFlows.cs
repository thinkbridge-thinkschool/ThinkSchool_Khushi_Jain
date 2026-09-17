using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocBook.Api.Tests;

internal sealed record Slot(DateTimeOffset Start, DateTimeOffset End);

// The booking flow as a caller drives it, so each test says what it is about and not how to sign in.
internal static class ApiFlows
{
    public const string PatientPassword = "correct-horse-battery";

    // A registration that already exists creates nothing, so every test needs an address of its own.
    public static string NewEmail() => $"patient-{Guid.NewGuid():N}@docbook.test";

    public static HttpClient Anonymous(this WebApplicationFactory<Program> factory) => factory.CreateClient();

    public static async Task<HttpClient> AsNewPatientAsync(this WebApplicationFactory<Program> factory, string email)
    {
        var client = factory.CreateClient();

        var registration = await client.PostAsJsonAsync("/api/v1/patients", new
        {
            fullName = "Ada Lovelace",
            email,
            phone = "0000000000",
            password = PatientPassword
        });

        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await TokenAsync(client, email, PatientPassword));

        return client;
    }

    public static async Task<HttpClient> AsStaffAsync(this WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await TokenAsync(client, DocBookApiFixture.StaffEmail, DocBookApiFixture.StaffPassword));

        return client;
    }

    public static async Task<string> TokenAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tokens", new { email, password });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("access_token").GetString()!;
    }

    public static async Task OpenDayAsync(HttpClient staff, Guid doctorId, DateOnly date)
    {
        var response = await staff.PostAsJsonAsync($"/api/v1/doctors/{doctorId}/days", new
        {
            date = date.ToString("yyyy-MM-dd"),
            opensAt = "09:00:00",
            closesAt = "17:00:00"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    public static async Task<IReadOnlyList<Slot>> FreeSlotsAsync(HttpClient client, Guid doctorId, DateOnly date)
    {
        var response = await client.GetAsync(
            $"/api/v1/doctors/{doctorId}/days/{date:yyyy-MM-dd}/free-slots?minutes=30");

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("slots")
            .EnumerateArray()
            .Select(slot => new Slot(
                slot.GetProperty("start").GetDateTimeOffset(),
                slot.GetProperty("end").GetDateTimeOffset()))
            .ToList();
    }

    public static Task<HttpResponseMessage> BookAsync(
        HttpClient client,
        Guid doctorId,
        Slot slot,
        Guid? patientId = null) =>
        client.PostAsJsonAsync("/api/v1/appointments", new
        {
            doctorId,
            start = slot.Start,
            end = slot.End,
            reason = "annual check-up",
            patientId
        });

    public static async Task<Guid> BookedAppointmentAsync(HttpClient client, Guid doctorId, Slot slot)
    {
        var response = await BookAsync(client, doctorId, slot);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("appointmentId").GetGuid();
    }

    // Always ahead of now, so the aggregate's "an appointment cannot start in the past" never fires.
    public static DateOnly Tomorrow() => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
}
