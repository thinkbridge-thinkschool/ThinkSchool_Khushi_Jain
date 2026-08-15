# AI Reflection

**What Claude got right.** It read `OrderService.CreateOrderAsync` accurately and lifted the pricing rules out
behind an `IOrderRule` abstraction, leaving the service to orchestrate rather than decide. What it did not do
is finish the idea: every rule landed in one `DefaultOrderRules` class holding three `if` blocks, registered
once in `Program.cs`. Adding a fourth rule still means editing that class, so the pattern was named rather than
delivered. Splitting it into a class per rule was my correction, not its suggestion.

**Where I caught a bug it kept.** Reading the diff, the tax line had survived untouched:
`totalTax += item.UnitPrice * 0.08m`, directly below a subtotal that multiplies by `item.Quantity`. Tax is
charged once per line instead of per unit. Claude preserved it faithfully because I asked for a refactor, and a
refactor does not question arithmetic. That is the argument for reading the diff rather than the summary.

**What Copilot saved.** From the comment `// Test: validation rejects orders with negative quantity` it
produced the Moq setup, the `Times.Never` verification and the assertion in a single pass — a few minutes per
test, repeated.

**Where Copilot was subtly wrong.** Twice. It hard-coded an expected total as `100m + 18.5m + 8m - 10m - 10m`,
re-encoding the implementation so the test cannot fail for the right reason. And it asserted
`items[0].Description == "No description"`, which locks in the service mutating the caller's own request list
as though that were intended behaviour.

**At 2 AM.** Copilot. Inline completion against the failing test, in the file I am already looking at, beats
briefing an agent when minutes matter. Claude is what I reach for the next morning, to fix the design that
caused the incident.
