// Matches CreateQuoteRequest in QuoteController.cs / Contracts/QuoteRequests.cs.
export interface CreateQuoteFormValue {
  author: string;
  text: string;
}

// The 201 body of POST /api/quotes: the Quote entity, same shape GET /api/quotes/{id} returns.
export interface CreatedQuote {
  id: number;
  author: string;
  text: string;
  ownerId: string | null;
  isDeleted: boolean;
}

// The automatic minimal-API validation filter's 400 body (RFC 9110 ValidationProblemDetails).
// Its `errors` keys are the C# record's property names as-is: PascalCase, not camelCase.
export interface ValidationProblemDetails {
  title: string;
  status: number;
  errors?: Record<string, string[]>;
}
