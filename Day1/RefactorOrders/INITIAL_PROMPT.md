# Initial AI Prompt

Create a deliberately bad but compilable `OrderController.cs` for an ASP.NET Core 10 Web API.

The purpose is to simulate a legacy "god-method" controller that will later be refactored.

Requirements:

- Around 300 lines of code.
- Use an `OrderController` with a giant `POST /api/orders` action.
- Mix business logic, validation, Entity Framework Core data access, and HTTP response shaping directly inside the controller.
- Include four empty `catch { }` blocks that swallow exceptions.
- Use synchronous EF Core calls inside an async controller action.
- Return `object` instead of typed responses.
- Include several realistic code smells and a couple of subtle bugs, including an off-by-one error and a possible null dereference.
- Keep the code realistic enough that it could plausibly be legacy production code.
- Do not refactor it.
- Make sure the project compiles.
- Include enough surrounding models/EF setup if necessary for the controller to compile and run.
- Do not add tests yet.

The goal is to create deliberately poor code that can later be refactored into Controller / Service / Repository layers using dependency injection.