#:package BCrypt.Net-Next@4.0.2

// Prints the hash for Staff:PasswordHash. The password stays in the shell and never reaches a file.

if (args is not [var password] || string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine("Usage: dotnet run staff-password-hash.cs -- <password>");
    return 1;
}

Console.WriteLine(BCrypt.Net.BCrypt.HashPassword(password));

return 0;
