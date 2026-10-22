import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BookingsList } from './bookings-list';
import { CatalogService } from '../../api/catalogs';
import { ApiError } from '../../generated/core/ApiError';
import { AttachmentEndpointsService } from '../../generated/services/AttachmentEndpointsService';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import type { AttachmentDto } from '../../generated/models/AttachmentDto';
import type { BookingDetailDto } from '../../generated/models/BookingDetailDto';
import type { BookingListItemDto } from '../../generated/models/BookingListItemDto';

/**
 * Beleg-Dialog (Schritt 7a): echte Komponentenklasse, API-Ebene und
 * Browser-APIs (fetch, Blob-URLs, window.open) sind simuliert.
 * Geprüft werden die Abnahmen: Dialog lädt Belegliste, Upload mit
 * Client-Vorvalidierung (Dateityp; die Größe entscheidet bewusst der
 * Server — keine unabhängige Client-Größensperre) und Split-Zuordnung,
 * Serverfehler (413/404/410) verständlich, Vorschau/Download über
 * Blob-URLs, Löschen mit Bestätigung — sowie die Dialog-Generation:
 * verzögerte Antworten eines älteren Dialogs (nach Dialogwechsel/
 * -schließung) überschreiben den aktuellen Dialog nicht, und ein
 * erfolgreicher Upload bleibt bei fehlgeschlagenem Listen-Refresh als
 * bestätigt erkennbar. Zusätzlich (Review 7a, Befund „Aktionsfehler“):
 * mit gerendertem Template wird geprüft, dass Aktionsfehler den
 * Dialoginhalt nicht verbergen — nach 413 bleibt eine kleinere Datei
 * auswählbar/sendbar, nach 410 der Entfernen-Button bedienbar, und
 * bei initialem Ladefehler ist „Erneut laden“ erreichbar.
 */

function listItem(bookingId: string, effectivelyReversed = false): BookingListItemDto {
  return {
    id: bookingId,
    type: 'Expense',
    date: '2026-10-01',
    note: null,
    totalAmount: 8000,
    baseRevision: 1,
    effectivelyReversed,
    accountName: 'Giro',
    splits: [],
    taxMarked: false,
  } as unknown as BookingListItemDto;
}

function detail(overrides: Partial<BookingDetailDto> = {}): BookingDetailDto {
  return {
    id: 'b1',
    type: 'Expense',
    date: '2026-10-01',
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
    splits: [
      {
        id: 's1',
        poolId: 'flex',
        poolName: 'Flex',
        amount: 5000,
        categoryId: 'cat-1',
        categoryName: 'Lebensmittel',
        tax: null,
        note: null,
      },
      {
        id: 's2',
        poolId: 'haushalt',
        poolName: 'Haushalt',
        amount: 3000,
        categoryId: 'cat-2',
        categoryName: 'Sonstiges',
        tax: null,
        note: null,
      },
    ],
    transferPurposes: [],
    reassignmentAccounts: [],
    totalAmount: 8000,
    refundedByPool: [],
    ...overrides,
  } as unknown as BookingDetailDto;
}

function attachment(overrides: Partial<AttachmentDto> = {}): AttachmentDto {
  return {
    id: 'att-1',
    originalFilename: 'quittung.pdf',
    contentType: 'application/pdf',
    sizeBytes: 4096,
    sha256: 'a'.repeat(64),
    splitId: null,
    originalSplitId: null,
    createdAtUtc: '2026-10-01T12:00:00Z',
    ...overrides,
  } as unknown as AttachmentDto;
}

function catalogStub(): CatalogService {
  return {
    accounts: signal([]),
    pools: signal([]),
    allAccounts: signal([]),
    allPools: signal([]),
    allCategories: signal([]),
    allTaxCategories: signal([]),
    categories: signal([]),
    taxCategories: signal([]),
    loading: signal(false),
    load: vi.fn().mockResolvedValue(undefined),
    refresh: vi.fn().mockResolvedValue(undefined),
  } as unknown as CatalogService;
}

function createComponent(): BookingsList {
  const { component } = createRendered();
  return component;
}

/** Komponente gerendert (detectChanges) anlegen — für
 *  Bedienbarkeits-Tests am tatsächlichen DOM (Review 7a, Befund
 *  „Aktionsfehler“: Fehler dürfen den Dialoginhalt nicht verbergen). */
function createRendered(): {
  component: BookingsList;
  fixture: ComponentFixture<BookingsList>;
  el: HTMLElement;
} {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [BookingsList],
    providers: [
      {
        provide: Router,
        useValue: { navigateByUrl: vi.fn().mockResolvedValue(true) },
      },
      { provide: CatalogService, useValue: catalogStub() },
    ],
  });
  const fixture = TestBed.createComponent(BookingsList);
  fixture.detectChanges();
  return {
    component: fixture.componentInstance,
    fixture,
    el: fixture.nativeElement as HTMLElement,
  };
}

function fileInputEvent(file: File | null): Event {
  const target = { files: file ? [file] : [], value: '' };
  return { target } as unknown as Event;
}

/** Synchroner Abbruch mit Fehler — return-typ `never` passt zu allen
 *  generierten Service-Signaturen (CancelablePromise). */
function unexpectedCall(name: string): never {
  throw new Error(`Unerwarteter API-Call: ${name}`);
}

function stubObjectUrls(): {
  createObjectURL: ReturnType<typeof vi.fn>;
  revokeObjectURL: ReturnType<typeof vi.fn>;
} {
  const createObjectURL = vi.fn(
    () => `blob:test-${createObjectURL.mock.calls.length}`,
  );
  const revokeObjectURL = vi.fn();
  vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL });
  return { createObjectURL, revokeObjectURL };
}

function stubBlobFetch(bytes = new Blob(['daten'])): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async () => new Response(bytes, { status: 200 }));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

beforeEach(() => {
  // Standard: unerwartete API-Calls werden sichtbar abgelehnt.
  vi.spyOn(JournalEndpointsService, 'listBookings').mockResolvedValue([] as never);
  // ngOnInit lädt Salden für das Panel (7.5) — hier ohne Daten, sonst
  // würde der generierte Client den globalen fetch berühren und der
  // Blob-Stub im Download-Test die Salden-Anfrage abfangen.
  vi.spyOn(JournalEndpointsService, 'getBalances').mockResolvedValue(null as never);
  vi.spyOn(JournalEndpointsService, 'getBooking').mockImplementation(() =>
    unexpectedCall('getBooking'),
  );
  vi.spyOn(AttachmentEndpointsService, 'getBookingAttachments').mockImplementation(
    () => unexpectedCall('getBookingAttachments'),
  );
  vi.spyOn(AttachmentEndpointsService, 'uploadAttachment').mockImplementation(() =>
    unexpectedCall('uploadAttachment'),
  );
  vi.spyOn(AttachmentEndpointsService, 'deleteAttachment').mockImplementation(() =>
    unexpectedCall('deleteAttachment'),
  );
  vi.spyOn(AttachmentEndpointsService, 'reassignAttachmentSplit').mockImplementation(
    () => unexpectedCall('reassignAttachmentSplit'),
  );
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('BookingsList Beleg-Dialog (Schritt 7a)', () => {
  it('öffnet den Dialog mit Detail und Belegliste; einzelne Split → Zuordnung bleibt „gesamte Ausgabe“', async () => {
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(
      detail({
        splits: [
          {
            id: 's1',
            poolId: 'flex',
            poolName: 'Flex',
            amount: 8000,
            categoryId: 'cat-1',
            categoryName: 'Lebensmittel',
            tax: null,
            note: null,
          },
        ],
      }),
    );
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue(
      [attachment()],
    );

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() =>
      expect(component.attachments()?.loading).toBe(false),
    );

    const state = component.attachments();
    expect(state?.attachments).toHaveLength(1);
    expect(state?.detail?.splits).toHaveLength(1);
    expect(state?.splitId).toBe('');
  });

  it('Upload: zulässiges PDF wird mit Split-Zuordnung gesendet und die Liste neu geladen', async () => {
    const upload = vi.mocked(AttachmentEndpointsService.uploadAttachment);
    upload.mockResolvedValue(attachment() as never);
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([attachment()]);

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() =>
      expect(component.attachments()?.loading).toBe(false),
    );

    const file = new File(['%PDF-1.4'], 'quittung.pdf', {
      type: 'application/pdf',
    });
    component.onFileSelected(fileInputEvent(file));
    component.attachments.update((d) => (d ? { ...d, splitId: 's2' } : d));

    expect(component.canUpload()).toBe(true);
    await component.uploadAttachment();

    expect(upload).toHaveBeenCalledTimes(1);
    const [payload] = upload.mock.calls[0];
    expect(payload.bookingId).toBe('b1');
    expect(payload.splitId).toBe('s2');
    expect(payload.file).toBe(file); // File (nicht nackter Blob) → Name bleibt erhalten

    const state = component.attachments();
    expect(state?.selectedFile).toBeNull(); // Auswahl nach Erfolg zurückgesetzt
    expect(state?.attachments).toHaveLength(1); // Liste neu geladen
  });

  it('Client-Vorvalidierung: unzulässiger Typ wird ohne API-Call abgelehnt', () => {
    const component = createComponent();
    component.openAttachments(listItem('b1'));

    const file = new File(['x'], 'notiz.txt', { type: 'text/plain' });
    component.onFileSelected(fileInputEvent(file));

    expect(component.attachments()?.clientError).toMatch(/nur PDF/);
    expect(component.canUpload()).toBe(false);
    expect(AttachmentEndpointsService.uploadAttachment).not.toHaveBeenCalled();
  });

  it('Client-Vorvalidierung: keine Client-Größensperre — Datei über 20 MB erreicht den Upload-Aufruf (Server entscheidet)', async () => {
    // Review 7a: ein höher konfiguriertes Server-Limit (z. B.
    // 30.000.000) darf nicht durch eine feste Client-Grenze ausgehebelt
    // werden — die Datei erreicht den Upload; ein zu großer Upload wird
    // serverseitig mit verständlichem 413 abgewiesen.
    const upload = vi.mocked(AttachmentEndpointsService.uploadAttachment);
    upload.mockResolvedValue(attachment() as never);
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments)
      .mockResolvedValueOnce([]) // initiales Laden
      .mockResolvedValueOnce([attachment()]); // Refresh nach Erfolg

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));

    const file = new File(['%PDF-1.4'], 'gross.pdf', {
      type: 'application/pdf',
    });
    // 25 MB > Server-Default 20.000.000 — wird clientseitig NICHT
    // gesperrt (size ist read-only, für den Stub reicht defineProperty).
    const bigFile = Object.defineProperty(file, 'size', { value: 25_000_000 });

    component.onFileSelected(fileInputEvent(bigFile));

    expect(component.attachments()?.clientError).toBeNull();
    expect(component.canUpload()).toBe(true);
    await component.uploadAttachment();

    expect(upload).toHaveBeenCalledTimes(1);
    expect((upload.mock.calls[0][0] as { file: File }).file).toBe(bigFile);
  });

  it('Serverfehler 413 wird im Dialog verständlich angezeigt, Dialog bleibt offen', async () => {
    const upload = vi.mocked(AttachmentEndpointsService.uploadAttachment);
    upload.mockRejectedValue(
      new ApiError(
        {} as never,
        {
          url: '/api/v1/attachments',
          ok: false,
          status: 413,
          statusText: 'Payload Too Large',
          body: {
            error: {
              code: 'validation_error',
              message: 'Datei überschreitet die maximale Größe von 10485760 Byte.',
            },
          },
        } as never,
        'Payload Too Large',
      ) as never,
    );
    const component = createComponent();
    component.openAttachments(listItem('b1'));

    const file = new File(['%PDF-1.4'], 'quittung.pdf', {
      type: 'application/pdf',
    });
    component.onFileSelected(fileInputEvent(file));
    await component.uploadAttachment();

    const state = component.attachments();
    expect(state).not.toBeNull(); // Dialog bleibt offen
    expect(state?.error?.message).toMatch(/maximale Größe/);
    expect(state?.selectedFile).toBe(file); // Auswahl bleibt zur Neuauswahl
  });

  it('Vorschau: Bild wird über fetch + Blob-URL geladen; neue Vorschau revokiert die alte URL', async () => {
    const { createObjectURL, revokeObjectURL } = stubObjectUrls();
    stubBlobFetch();
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue([
      attachment({ contentType: 'image/png', originalFilename: 'scan.png' }),
    ]);

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() =>
      expect(component.attachments()?.loading).toBe(false),
    );
    const image = component.attachments()?.attachments[0];
    expect(image).toBeDefined();

    await component.showPreview(image!);
    expect(component.attachments()?.previewUrl).toBe(
      createObjectURL.mock.results[0].value,
    );
    expect(component.attachments()?.previewId).toBe(image!.id);

    // Zweite Vorschau (gleicher Beleg) → neue URL, alte wird revokiert.
    await component.showPreview(image!);
    expect(component.attachments()?.previewUrl).toBe(
      createObjectURL.mock.results[1].value,
    );
    expect(revokeObjectURL).toHaveBeenCalledTimes(1);
    expect(revokeObjectURL).toHaveBeenCalledWith(
      createObjectURL.mock.results[0].value,
    );
  });

  it('PDF öffnen: window.open bekommt die Blob-URL des Downloads', async () => {
    const { createObjectURL } = stubObjectUrls();
    stubBlobFetch();
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue([
      attachment(),
    ]);
    const openMock = vi.spyOn(window, 'open').mockImplementation(() => null);

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() =>
      expect(component.attachments()?.loading).toBe(false),
    );
    const pdf = component.attachments()?.attachments[0];
    expect(pdf?.contentType).toBe('application/pdf');

    await component.openAttachment(pdf!);

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch).toHaveBeenCalledWith(
      '/api/v1/attachments/att-1/download',
      expect.objectContaining({}),
    );
    expect(openMock).toHaveBeenCalledWith(
      createObjectURL.mock.results[0].value,
      '_blank',
      'noopener',
    );
  });

  it('Herunterladen: temporäre <a download>-Brücke mit Original-Dateinamen', async () => {
    stubObjectUrls();
    stubBlobFetch();
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue([
      attachment({ originalFilename: 'quittung.pdf' }),
    ]);
    const clickSpy = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => undefined);

    const component = createComponent();

    // Erst jetzt: TestBed hängt das Komponenten-Root-Element über
    // appendChild in den body; der Spy würde das verhindern.
    const appendSpy = vi
      .spyOn(document.body, 'appendChild')
      .mockImplementation((node) => node);
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() =>
      expect(component.attachments()?.loading).toBe(false),
    );
    const pdf = component.attachments()?.attachments[0];

    await component.downloadAttachment(pdf!);

    expect(appendSpy).toHaveBeenCalledTimes(1);
    const anchor = appendSpy.mock.calls[0][0] as HTMLAnchorElement;
    expect(anchor.download).toBe('quittung.pdf');
    expect(clickSpy).toHaveBeenCalledTimes(1);
  });

  it('Löschen: Bestätigung ruft die API auf; gelöschte Vorschau wird aufgeräumt', async () => {
    const { revokeObjectURL } = stubObjectUrls();
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments)
      .mockResolvedValueOnce([
        attachment({ id: 'att-1', contentType: 'image/png' }),
        attachment({ id: 'att-2', contentType: 'image/jpeg' }),
      ])
      .mockResolvedValueOnce([attachment({ id: 'att-2', contentType: 'image/jpeg' })]);
    const deleteMock = vi.mocked(AttachmentEndpointsService.deleteAttachment);
    deleteMock.mockResolvedValue(undefined as never);

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() =>
      expect(component.attachments()?.loading).toBe(false),
    );

    // att-1 ist die angezeigte Vorschau; att-2 wird gelöscht.
    component.attachments.update((d) =>
      d
        ? {
            ...d,
            previewId: 'att-1',
            previewUrl: 'blob:test-1',
            confirmDeleteId: 'att-2',
          }
        : d,
    );
    await component.confirmDeleteAttachment();

    expect(deleteMock).toHaveBeenCalledWith('att-2');
    const state = component.attachments();
    expect(state?.confirmDeleteId).toBeNull();
    expect(state?.attachments.map((a) => a.id)).toEqual(['att-2']);
    // Vorschau von att-1 bleibt — nur die URL des gelöschten Belegs wird revokiert.
    expect(state?.previewId).toBe('att-1');
    expect(revokeObjectURL).not.toHaveBeenCalled();

    // Nun att-1 (die Vorschau) löschen → Vorschau wird geräumt und revokiert.
    component.attachments.update((d) => (d ? { ...d, confirmDeleteId: 'att-1' } : d));
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue(
      [] as never,
    );
    await component.confirmDeleteAttachment();
    expect(component.attachments()?.previewId).toBeNull();
    expect(component.attachments()?.previewUrl).toBeNull();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:test-1');
  });

  // ---------- Dialog-Generation (Review: verzögerte Antworten) ----------

  /** Ein Makro-Tick, damit verspänte Promise-Ketten abarbeiten dürfen. */
  function tick(): Promise<void> {
    return new Promise((r) => setTimeout(r, 0));
  }

  /**
   * Buchungen 'a' und 'b'; die Antworten für 'a' hängen an einem
   * Deferred, das der Test manuell auflöst — so lässt sich eine
   * verzögerte Antwort nach Dialogwechsel/-schließung simulieren.
   */
  function deferredLoadsForA(): (value: [BookingDetailDto, AttachmentDto[]]) => void {
    let resolve!: (value: [BookingDetailDto, AttachmentDto[]]) => void;
    const done = new Promise<[BookingDetailDto, AttachmentDto[]]>((r) => {
      resolve = r;
    });
    // Die generierten Signaturen liefern CancelablePromise<T> — die
    // Deferred-Stubs werden daher als `never` gecastet.
    vi.mocked(JournalEndpointsService.getBooking).mockImplementation(
      ((id: string) =>
        id === 'a' ? done.then((x) => x[0]) : Promise.resolve(detail())) as never,
    );
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockImplementation(
      ((id: string) =>
        (id === 'a'
          ? done.then((x) => x[1])
          : Promise.resolve([attachment()]))) as never,
    );
    return resolve;
  }

  it('verzögerte Ladeantwort nach Dialogwechsel überschreibt den neuen Dialog nicht', async () => {
    const resolveA = deferredLoadsForA();

    const component = createComponent();
    component.openAttachments(listItem('a'));
    component.openAttachments(listItem('b'));
    await vi.waitFor(() => {
      expect(component.attachments()?.loading).toBe(false);
      expect(component.attachments()?.booking.id).toBe('b');
    });

    // Antwort für 'a' kommt jetzt verspätet an:
    resolveA([detail({ id: 'a' }), []]);
    await tick();

    const state = component.attachments();
    expect(state?.booking.id).toBe('b');
    expect(state?.attachments).toHaveLength(1); // Liste von 'b' bleibt intakt
    expect(state?.error).toBeNull();
  });

  it('verzögerte Ladeantwort nach Dialogschließung öffnet keinen Zustand nach', async () => {
    const resolveA = deferredLoadsForA();

    const component = createComponent();
    component.openAttachments(listItem('a'));
    component.closeAttachments();
    expect(component.attachments()).toBeNull();

    resolveA([detail({ id: 'a' }), []]);
    await tick();

    expect(component.attachments()).toBeNull(); // Dialog bleibt geschlossen
  });

  it('verzögerte Vorschau-Antwort nach Dialogwechsel setzt keine Vorschau im neuen Dialog', async () => {
    const { createObjectURL } = stubObjectUrls();
    let resolveFetch!: (response: Response) => void;
    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>((r) => {
            resolveFetch = r;
          }),
      ),
    );
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue([
      attachment({ contentType: 'image/png' }),
    ]);

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    const image = component.attachments()!.attachments[0];
    const previewCall = component.showPreview(image);

    // Dialog wechseln, während der Blob-Download von 'b1' noch läuft:
    component.openAttachments(listItem('b2'));
    await vi.waitFor(() => {
      expect(component.attachments()?.booking.id).toBe('b2');
      expect(component.attachments()?.loading).toBe(false);
    });

    resolveFetch(new Response(new Blob(['daten']), { status: 200 }));
    await previewCall;
    await tick();

    const state = component.attachments()!;
    expect(state.booking.id).toBe('b2');
    expect(state.previewId).toBeNull();
    expect(state.previewUrl).toBeNull();
    expect(createObjectURL).not.toHaveBeenCalled(); // keine neue (leakende) URL
  });

  it('verzögerte Lösch-Antwort nach Dialogwechsel verändert den neuen Dialog nicht', async () => {
    let resolveDelete!: () => void;
    const deleteMock = vi.mocked(AttachmentEndpointsService.deleteAttachment);
    // Die generierte Signatur liefert CancelablePromise<void> — der
    // Deferred-Stub wird daher als `never` gecastet.
    deleteMock.mockImplementation(
      (() =>
        new Promise<void>((r) => {
          resolveDelete = r;
        })) as never,
    );
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue([
      attachment({ id: 'att-1' }),
      attachment({ id: 'att-2' }),
    ]);

    const component = createComponent();
    component.openAttachments(listItem('a'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    component.requestDeleteAttachment(attachment({ id: 'att-1' }));
    const deleteCall = component.confirmDeleteAttachment();

    // Dialog wechseln, während der Delete noch läuft:
    component.openAttachments(listItem('b'));
    await vi.waitFor(() => {
      expect(component.attachments()?.booking.id).toBe('b');
      expect(component.attachments()?.loading).toBe(false);
    });

    resolveDelete();
    await deleteCall;
    await tick();

    const state = component.attachments()!;
    expect(state.booking.id).toBe('b');
    expect(state.attachments.map((x) => x.id)).toEqual(['att-1', 'att-2']);
    expect(state.busy).toBe(false);
    expect(state.confirmDeleteId).toBeNull();
    expect(state.error).toBeNull();
  });

  it('Upload: Erfolg + fehlgeschlagener Listen-Refresh bleiben als bestätigter Erfolg erkennbar', async () => {
    const upload = vi.mocked(AttachmentEndpointsService.uploadAttachment);
    upload.mockResolvedValue(attachment() as never);
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments)
      .mockResolvedValueOnce([]) // initiales Laden
      .mockRejectedValueOnce(new Error('Netzwerk')) // Refresh nach dem Upload

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));

    const file = new File(['%PDF-1.4'], 'quittung.pdf', {
      type: 'application/pdf',
    });
    component.onFileSelected(fileInputEvent(file));
    await component.uploadAttachment();

    expect(upload).toHaveBeenCalledTimes(1);
    const state = component.attachments()!;
    expect(state.success).toBe('Beleg hochgeladen.');
    expect(state.error?.message).toMatch(/Netzwerk/);
    expect(state.selectedFile).toBeNull(); // kein erneuter Upload derselben Datei
    expect(state.busy).toBe(false);
  });

  it('Download-Fehler 404 wird im Dialog verständlich angezeigt', async () => {
    stubObjectUrls();
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 404, statusText: 'Not Found' })),
    );
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue([
      attachment({ contentType: 'image/png' }),
    ]);

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    const image = component.attachments()!.attachments[0];

    await component.showPreview(image);

    const state = component.attachments()!;
    expect(state.error?.message).toMatch(/nicht gefunden oder wurde bereits entfernt/);
    expect(state.previewUrl).toBeNull();
    expect(state.busyAttachmentId).toBeNull();
  });

  it('Download-Fehler 410 wird im Dialog verständlich angezeigt', async () => {
    stubObjectUrls();
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 410, statusText: 'Gone' })),
    );
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue([
      attachment(),
    ]);

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    const pdf = component.attachments()!.attachments[0];

    await component.downloadAttachment(pdf);

    const state = component.attachments()!;
    expect(state.error?.message).toMatch(/nicht mehr verfügbar \(410\)/);
    expect(state.busyAttachmentId).toBeNull();
  });

  // ---------- Aktionsfehler lassen den Dialog bedienbar (Review 7a) ----------
  //
  // Diese Tests prüfen nicht nur Zustandsfelder/Meldungstexte, sondern
  // das gerenderte Template: nach einem Aktionsfehler müssen Datei-
  // auswahl, Upload- und Entfernen-Buttons vorhanden und nicht gesperrt
  // sein.

  it('Aktionsfehler 413: Belegliste und Upload bleiben bedienbar — kleinere Datei kann ausgewählt und gesendet werden', async () => {
    const upload = vi.mocked(AttachmentEndpointsService.uploadAttachment);
    upload
      .mockRejectedValueOnce(
        new ApiError(
          {} as never,
          {
            url: '/api/v1/attachments',
            ok: false,
            status: 413,
            statusText: 'Payload Too Large',
            body: {
              error: {
                code: 'validation_error',
                message: 'Die Datei überschreitet die maximale Größe von 104857600 Byte.',
              },
            },
          } as never,
          'Payload Too Large',
        ) as never,
      )
      .mockResolvedValueOnce(attachment() as never);
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments)
      .mockResolvedValueOnce([]) // initiales Laden
      .mockResolvedValueOnce([attachment()]); // Refresh nach Erfolg

    const { component, fixture, el } = createRendered();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    fixture.detectChanges();

    const big = new File(['%PDF-1.4'], 'gross.pdf', {
      type: 'application/pdf',
    });
    component.onFileSelected(fileInputEvent(big));
    await component.uploadAttachment();
    fixture.detectChanges();

    // Fehlerbanner ist da — aber die Inhalte werden NICHT ersetzt:
    expect(el.querySelector('.banner-error')?.textContent).toMatch(
      /maximale Größe/,
    );
    const fileInput = el.querySelector('input[type="file"]') as HTMLInputElement;
    expect(fileInput).not.toBeNull();
    expect(fileInput.disabled).toBe(false);
    const uploadButton = [...el.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Beleg hochladen'),
    );
    expect(uploadButton).toBeTruthy();
    expect(uploadButton?.disabled).toBe(false);

    // Kleinere Datei auswählen und erneut senden — der Upload-Aufruf
    // erreicht die API.
    const small = new File(['%PDF-1.4'], 'klein.pdf', {
      type: 'application/pdf',
    });
    component.onFileSelected(fileInputEvent(small));
    fixture.detectChanges();
    expect(
      (el.querySelector('input[type="file"]') as HTMLInputElement).disabled,
    ).toBe(false);
    await component.uploadAttachment();

    expect(upload).toHaveBeenCalledTimes(2);
    expect((upload.mock.calls[1][0] as { file: File }).file).toBe(small);
    expect(component.attachments()?.error).toBeNull();
    expect(component.attachments()?.attachments).toHaveLength(1);
  });

  it('Aktionsfehler 410 (Vorschau): Belegliste und Entfernen-Buttons bleiben bedienbar — betroffener Beleg kann entfernt werden', async () => {
    stubObjectUrls();
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 410, statusText: 'Gone' })),
    );
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue(
      [
        attachment({
          id: 'att-1',
          contentType: 'image/png',
          originalFilename: 'scan.png',
        }),
        attachment({ id: 'att-2', originalFilename: 'zweit.pdf' }),
      ],
    );
    vi.mocked(AttachmentEndpointsService.deleteAttachment).mockResolvedValue(
      undefined as never,
    );

    const { component, fixture, el } = createRendered();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    fixture.detectChanges();

    await component.showPreview(component.attachments()!.attachments[0]);
    fixture.detectChanges();

    expect(el.querySelector('.banner-error')?.textContent).toMatch(/410/);
    // Beide Belege bleiben in der Liste …
    expect(el.querySelectorAll('.attachment-item')).toHaveLength(2);
    // … und alle Entfernen-Buttons sind bedienbar (nicht gesperrt).
    const deleteButtons = [...el.querySelectorAll('button')].filter(
      (b) => b.textContent?.trim() === 'Entfernen',
    );
    expect(deleteButtons).toHaveLength(2);
    expect(deleteButtons.every((b) => !b.disabled)).toBe(true);

    // Betroffenenen Beleg entfernen — der Delete-Aufruf erreicht die API.
    component.requestDeleteAttachment(
      attachment({
        id: 'att-1',
        contentType: 'image/png',
        originalFilename: 'scan.png',
      }),
    );
    fixture.detectChanges();
    const confirm = [...el.querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === 'Ja, entfernen',
    );
    expect(confirm).toBeTruthy();
    expect(confirm?.disabled).toBe(false);
    await component.confirmDeleteAttachment();

    expect(AttachmentEndpointsService.deleteAttachment).toHaveBeenCalledTimes(1);
    expect(AttachmentEndpointsService.deleteAttachment).toHaveBeenCalledWith(
      'att-1',
    );
  });

  it('Initialer Ladefehler: „Erneut laden“ ist erreichbar und lädt erfolgreich', async () => {
    vi.mocked(JournalEndpointsService.getBooking)
      .mockRejectedValueOnce(new Error('Netzwerk'))
      .mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments)
      .mockResolvedValueOnce([])
      .mockResolvedValue([attachment()]);

    const { component, fixture, el } = createRendered();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    fixture.detectChanges();

    // Initiales Laden fehlgeschlagen: Detail fehlt, Fehlerbanner da —
    // kein Sackgassen-Zweig, sondern „Erneut laden“.
    expect(component.attachments()?.detail).toBeNull();
    expect(el.querySelector('.banner-error')).not.toBeNull();
    const retry = [...el.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Erneut laden'),
    ) as HTMLButtonElement;
    expect(retry).toBeTruthy();
    expect(retry.disabled).toBe(false);

    retry.click();
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    fixture.detectChanges();

    expect(component.attachments()?.detail?.id).toBe('b1');
    expect(component.attachments()?.error).toBeNull();
    expect(el.querySelectorAll('.attachment-item')).toHaveLength(1);
    expect(el.querySelector('.banner-error')).toBeNull();
  });

  // ---------- Umzuordnung bestehender Belege (Nachtrag N1) ----------

  it('Umzuordnung: Auswahl wird per PATCH gesendet und die Liste mit der neuen Zuordnung neu geladen', async () => {
    const reassign = vi.mocked(AttachmentEndpointsService.reassignAttachmentSplit);
    reassign.mockResolvedValue(attachment({ splitId: 's2' }) as never);
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments)
      .mockResolvedValueOnce([attachment()]) // initiales Laden (gesamte Ausgabe)
      .mockResolvedValueOnce([attachment({ splitId: 's2' })]); // Refresh nach Erfolg

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));

    const att = component.attachments()!.attachments[0];
    component.onReassignSelected(att, {
      target: { value: 's2' },
    } as unknown as Event);
    expect(component.reassignDirty(att)).toBe(true);

    await component.reassignAttachment(att);

    expect(reassign).toHaveBeenCalledTimes(1);
    expect(reassign).toHaveBeenCalledWith('att-1', { splitId: 's2' });
    const state = component.attachments();
    expect(state?.error).toBeNull();
    // Liste neu geladen: neue Zuordnung ist in der Zeile sichtbar
    // (Label + initialisierte Auswahl) — wie beim Upload ersetzt der
    // Refresh den Erfolgshinweis.
    expect(state?.attachments).toHaveLength(1);
    expect(state!.attachments[0].splitId).toBe('s2');
    expect(state!.attachments[0].originalFilename).toBe('quittung.pdf');
    expect(component.reassignDirty(state!.attachments[0])).toBe(false);
    expect(
      component.attachmentSplitLabel(state!.attachments[0]),
    ).toContain('Haushalt');
  });

  it('Umzuordnung: zurück zur gesamten Ausgabe wird mit splitId null gesendet', async () => {
    const reassign = vi.mocked(AttachmentEndpointsService.reassignAttachmentSplit);
    reassign.mockResolvedValue(attachment() as never); // splitId null
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments)
      .mockResolvedValueOnce([attachment({ splitId: 's1' })]) // initiales Laden
      .mockResolvedValueOnce([attachment()]); // Refresh nach Erfolg

    const component = createComponent();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));

    const att = component.attachments()!.attachments[0];
    expect(att.splitId).toBe('s1');
    component.onReassignSelected(att, { target: { value: '' } } as unknown as Event);
    expect(component.reassignDirty(att)).toBe(true);

    await component.reassignAttachment(att);

    expect(reassign).toHaveBeenCalledTimes(1);
    expect(reassign).toHaveBeenCalledWith('att-1', { splitId: null });
    expect(component.attachments()!.attachments[0].splitId).toBeNull();
    expect(component.attachmentSplitLabel(component.attachments()!.attachments[0])).toBe(
      'Gesamte Ausgabe',
    );
  });

  it('Umzuordnung: „Anwenden“ ist nur bei Abweichung von der serverseitigen Zuordnung wirksam', async () => {
    const reassign = vi.mocked(AttachmentEndpointsService.reassignAttachmentSplit);
    reassign.mockResolvedValue(attachment() as never);
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(detail());
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue([
      attachment(), // splitId null → serverseitig „gesamte Ausgabe“
    ]);

    const { component, fixture, el } = createRendered();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    fixture.detectChanges();

    const att = component.attachments()!.attachments[0];
    const apply = () =>
      [...el.querySelectorAll('button')].find(
        (b) => b.textContent?.trim() === 'Anwenden',
      ) as HTMLButtonElement;
    // Auswahl = serverseitiger Zustand → keine Abweichung → gesperrt.
    expect(apply().disabled).toBe(true);

    component.onReassignSelected(att, {
      target: { value: 's2' },
    } as unknown as Event);
    fixture.detectChanges();
    expect(apply().disabled).toBe(false);

    // Ohne Aufruf: bloße Auswahl ändert nichts (kein API-Call).
    expect(reassign).not.toHaveBeenCalled();
  });

  it('Umzuordnung: keine Zuordnungszeile ohne Splits und ohne Split-Zuordnung', async () => {
    vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(
      detail({ splits: [] }),
    );
    vi.mocked(AttachmentEndpointsService.getBookingAttachments).mockResolvedValue([
      attachment(), // splitId null
    ]);

    const { component, fixture, el } = createRendered();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    fixture.detectChanges();

    expect(el.querySelector('.attachment-item')).not.toBeNull();
    expect(el.querySelector('.attachment-reassign')).toBeNull();
  });

  it('Umzuordnungsfehler 400 invalid_reference: Fehlerbanner neben dem Inhalt, Dialog bleibt offen, Liste zeigt den aktuellen Buchungszustand', async () => {
    // Szenario aus dem Nutzerfeedback: Ziel-Split wurde zwischen
    // Auswahl und „Anwenden“ von einer Korrektur entfernt → 400.
    const reassign = vi.mocked(AttachmentEndpointsService.reassignAttachmentSplit);
    reassign.mockRejectedValue(
      new ApiError(
        {} as never,
        {
          url: '/api/v1/attachments/att-1',
          ok: false,
          status: 400,
          statusText: 'Bad Request',
          body: {
            error: {
              code: 'invalid_reference',
              message: 'Der angegebene Split gehört nicht mehr zur Buchung.',
            },
          },
        } as never,
        'Bad Request',
      ) as never,
    );
    // Vorher: zwei Splits; nach dem Fehler-Refresh ist s2 weg
    // (Korrektur) und die Belegzuordnung serverseitig unverändert.
    const s1 = detail().splits[0];
    vi.mocked(JournalEndpointsService.getBooking)
      .mockResolvedValueOnce(detail())
      .mockResolvedValueOnce(detail({ splits: [s1] }));
    vi.mocked(AttachmentEndpointsService.getBookingAttachments)
      .mockResolvedValueOnce([attachment({ splitId: 's1' })])
      .mockResolvedValueOnce([attachment({ splitId: 's1' })]);

    const { component, fixture, el } = createRendered();
    component.openAttachments(listItem('b1'));
    await vi.waitFor(() => expect(component.attachments()?.loading).toBe(false));
    fixture.detectChanges();

    const select = el.querySelector('.attachment-reassign select') as HTMLSelectElement;
    expect(select).not.toBeNull();
    expect(select.options.length).toBe(3); // gesamte Ausgabe + s1 + s2

    const att = component.attachments()!.attachments[0];
    select.value = 's2';
    component.onReassignSelected(att, {
      target: { value: 's2' },
    } as unknown as Event);
    fixture.detectChanges();

    await component.reassignAttachment(att);
    fixture.detectChanges();

    // Fehlerbanner NEBEN dem Inhalt — Dialog bleibt offen, Belegzeile
    // bleibt sichtbar (kein Dialogneustart, Design D7).
    expect(el.querySelector('.banner-error')?.textContent).toMatch(
      /nicht mehr zur Buchung/,
    );
    expect(el.querySelectorAll('.attachment-item')).toHaveLength(1);
    // Fehler-Refresh: aktuelle Splits in den Optionen — s2 ist weg,
    // die Auswahl ist auf den serverseitigen Zustand zurückgesetzt.
    const refreshed = el.querySelector('.attachment-reassign select') as HTMLSelectElement;
    expect(refreshed.options.length).toBe(2); // gesamte Ausgabe + s1
    expect(refreshed.value).toBe('s1');
    expect(component.reassignDirty(attachment({ splitId: 's1' }))).toBe(false);
  });
});
