import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { API_BASE_URL, type QuoteDetail, type QuotesPage } from './http';

/**
 * The two Week-1 read endpoints, in one place so the eagerly loaded list page
 * and the lazily loaded detail page can share them without either importing
 * the other.
 */
@Injectable({ providedIn: 'root' })
export class QuotesApiService {
  private readonly http = inject(HttpClient);
  private readonly quotes = `${inject(API_BASE_URL)}/api/quotes`;

  // GET /api/quotes?page=&size= -> { page, size, total, items: [...] }
  getPage(page: number, size: number) {
    return this.http.get<QuotesPage>(this.quotes, { params: { page, size } });
  }

  // GET /api/quotes/{id} -> the Quote entity, or 404 with a zero-length body.
  getById(id: number) {
    return this.http.get<QuoteDetail>(`${this.quotes}/${id}`);
  }
}

/**
 * The API's id is an int, and its route is constrained to `{id:int}`, so
 * anything that is not a run of digits is not an id this API can answer for.
 * Parsing here rather than in the page keeps the rule with the endpoint it
 * describes.
 */
export function parseQuoteId(value: string | null | undefined): number | null {
  if (!value || !/^\d+$/.test(value)) {
    return null;
  }

  const id = Number(value);

  return Number.isSafeInteger(id) && id > 0 ? id : null;
}
