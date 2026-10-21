import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
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
 * Client-Vorvalidierung (Typ/Große) und Split-Zuordnung, Serverfehler
 * (413/404/410) verständlich, Vorschau/Download über Blob-URLs, Löschen
 * mit Bestätigung — sowie die Dialog-Generation: verzögerte Antworten
 * eines älteren Dialogs (nach Dialogwechsel/-schließung) überschreiben
 * den aktuellen Dialog nicht, und ein erfolgreicher Upload bleibt bei
 * fehlgeschlagenem Listen-Refresh als bestätigt erkennbar.
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
  return TestBed.createComponent(BookingsList).componentInstance;
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

  it('Client-Vorvalidierung: Datei über 20 MB (dezimal) wird ohne API-Call abgelehnt', () => {
    const component = createComponent();
    component.openAttachments(listItem('b1'));

    const file = new File(['x'], 'gross.pdf', {
      type: 'application/pdf',
    });
    // size ist read-only — für die Vorvalidierung reicht ein Stub.
    // 20_000_000 ist die dezimale Standardgrenze (Server-Default).
    const bigFile = Object.defineProperty(file, 'size', { value: 20_000_001 });

    component.onFileSelected(fileInputEvent(bigFile));

    expect(component.attachments()?.clientError).toMatch(
      /zu groß \(maximal 20\.000\.000 Byte\)/,
    );
    expect(component.canUpload()).toBe(false);
    expect(AttachmentEndpointsService.uploadAttachment).not.toHaveBeenCalled();
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
});
