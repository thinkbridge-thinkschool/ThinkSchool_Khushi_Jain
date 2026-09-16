namespace DocBook.Notifications.Infrastructure;

public sealed class EmailOptions
{
    public const string Section = "Notifications:Email";

    public const string MissingSettingsMessage =
        "Notifications:Email:ConnectionString and Notifications:Email:FromAddress are both required when email is enabled.";

    public bool Enabled { get; init; }

    public string? ConnectionString { get; init; }

    public string? FromAddress { get; init; }

    public bool IsUsable =>
        !Enabled || (!string.IsNullOrWhiteSpace(ConnectionString) && !string.IsNullOrWhiteSpace(FromAddress));
}
