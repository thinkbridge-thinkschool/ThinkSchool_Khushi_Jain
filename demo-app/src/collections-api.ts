import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { API_BASE_URL } from './http';

/**
 * The 201 body of POST /api/collections, which is CollectionController's
 * ToResponse: the aggregate, not the read model. It has no itemCount, and its
 * items carry only quoteId and addedAt -- no author, no text.
 */
export interface CreatedCollection {
  id: number;
  name: string;
  ownerId: string;
  items: { quoteId: number; addedAt: string }[];
}

/**
 * The 200 body of GET /api/collections/{id}, which is CollectionDetailsQuery's
 * read model. A different shape from the one above, so the two are never
 * interchangeable: this one has itemCount, and flattens each membership onto
 * the quote's author and text.
 */
export interface CollectionDetails {
  id: number;
  name: string;
  ownerId: string;
  itemCount: number;
  items: CollectionDetailsItem[];
}

export interface CollectionDetailsItem {
  quoteId: number;
  author: string;
  text: string;
  addedAt: string;
}

// Collection.MaximumItems in Models/Collection.cs. The API rejects the 51st add
// with a 400, so this is only used to disable the button before that happens.
export const MAXIMUM_ITEMS = 50;

// Collection.MinimumNameLength / MaximumNameLength.
export const MINIMUM_NAME_LENGTH = 3;
export const MAXIMUM_NAME_LENGTH = 80;

@Injectable({ providedIn: 'root' })
export class CollectionsApiService {
  private readonly http = inject(HttpClient);
  private readonly collections = `${inject(API_BASE_URL)}/api/collections`;

  // 201 with the aggregate shape. Requires the can-edit-quotes policy.
  create(name: string) {
    return this.http.post<CreatedCollection>(this.collections, { name });
  }

  // 200 with the read model, or 404. The only anonymous endpoint of the four.
  getById(id: number) {
    return this.http.get<CollectionDetails>(`${this.collections}/${id}`);
  }

  // 204 on success. 404 when no collection has that id -- note, NOT when the
  // quote does not exist. 400 for a duplicate or a full collection, carrying
  // the domain message in `detail` rather than in an `errors` dictionary.
  addItem(collectionId: number, quoteId: number) {
    return this.http.post<void>(`${this.collections}/${collectionId}/items`, { quoteId });
  }

  // 204 on success, 404 for an unknown collection, 400 when the quote is not a
  // member -- again as `detail`, not `errors`.
  removeItem(collectionId: number, quoteId: number) {
    return this.http.delete<void>(`${this.collections}/${collectionId}/items/${quoteId}`);
  }
}
