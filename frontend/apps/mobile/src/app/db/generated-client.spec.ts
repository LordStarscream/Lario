/**
 * Import-Check für den generierten OpenAPI-Client (openapi-typescript-codegen).
 *
 * Stellt sicher, dass die für Schritt 8 relevanten Symbole aus dem
 * generierten Code importierbar sind und die erwarteten Formen haben:
 * die Service-Klassen mit ihren Endpunkt-Methoden sowie die
 * `BookingCreatedDto`-Typdefinition.
 */
// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';

import type { BookingCreatedDto } from '../../generated';
import { LarioHostService, MasterDataEndpointsService } from '../../generated';

describe('Generierter OpenAPI-Client (Import-Check)', () => {
  it('MasterDataEndpointsService.listAccounts ist eine Funktion', () => {
    expect(typeof MasterDataEndpointsService.listAccounts).toBe('function');
  });

  it('LarioHostService.authLogin ist eine Funktion', () => {
    expect(typeof LarioHostService.authLogin).toBe('function');
  });

  it('BookingCreatedDto ist als Typ importierbar und typisiert', () => {
    // Typ-Referenz-Check: die Literalfelder müssen exakt zur generierten
    // Definition passen (models/BookingCreatedDto.ts:
    // { booking: BookingDetailDto; idempotent: boolean }).
    const _dto: BookingCreatedDto = {
      booking: {
        id: '',
        type: 'expense',
        date: '2024-05-01',
        note: null,
        baseRevision: 1,
        effectivelyReversed: false,
        fundingMonth: null,
        reversesBookingId: null,
        reversalBookingIds: [],
        refundsExpenseId: null,
        relatedSettlementId: null,
        accountPostings: [],
        poolPostings: [],
        splits: [],
        transferPurposes: [],
        reassignmentAccounts: [],
        totalAmount: 0,
        refundedByPool: [],
      },
      idempotent: false,
    };

    expect(typeof _dto).toBe('object');
  });
});
