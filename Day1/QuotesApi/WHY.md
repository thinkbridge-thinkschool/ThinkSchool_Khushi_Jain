\# Why a Rich Domain Model?



The original Quote model was anemic because it only contained public properties and had no behavior or business rules of its own. Validation was handled by the API endpoint, which meant the Quote entity itself could be created without enforcing its invariants.



The rich model moves these rules into the Quote entity. `Quote.Create()` is now the controlled creation point and ensures that the author and text are valid before a Quote can exist. Author is limited to 200 characters and text to 1000 characters. The properties also have private setters, preventing application code from changing the quote's state directly.



Soft deletion is modeled as domain behavior through `SoftDelete()`, rather than allowing application code to manipulate the deletion flag directly.



A concrete bug the anemic model could ship is an internal caller creating a Quote with an empty author or 1500-character text. The API's validation might catch normal HTTP requests, but another code path could bypass that validation and persist invalid data. With the rich model, `Quote.Create()` rejects the invalid state regardless of where the object is created.



This keeps business rules close to the data they protect and makes those rules easy to test without a database or API.

