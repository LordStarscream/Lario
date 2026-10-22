import { ChangeDetectionStrategy, Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import { CatalogService } from '../../api/catalogs';
import { formatBytes, formatCents, formatDate, parseEuroToCents, todayIso } from '../../api/money';
import { fetchAttachmentBlob } from '../../api/attachment-download';
import { BalancesPanel } from '../shared/balances-panel/balances-panel';
import { AttachmentEndpointsService } from '../../generated/services/AttachmentEndpointsService';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import type { AttachmentDto } from '../../generated/models/AttachmentDto';
import type { BalancesDto } from '../../generated/models/BalancesDto';
import type { BookingDetailDto } from '../../generated/models/BookingDetailDto';
import type { BookingListItemDto } from '../../generated/models/BookingListItemDto';

export interface ReversalDialogState {
  booking: BookingListItemDto;
  /** Stabile Identität des Stornos (Idempotenz), einmal pro Dialog erzeugt. */
  operationId: string;
  date: string;
  note: string;
  busy: boolean;
  error: ApiErrorInfo | null;
}

export interface RefundLineState {
  poolId: string;
  poolName: string | null;
  /** Verbleibender Deckel in Zenten: Split-Position des Pools minus bereits erstattet. */
  maxCents: number;
  /** Bereits gewährte Erstattungen an diesen Pool in Zenten (Info-Anzeige). */
  refundedCents: number;
  amountText: string;
}

export interface RefundDialogState {
  booking: BookingListItemDto;
  operationId: string;
  loading: boolean;
  busy: boolean;
  detail: BookingDetailDto | null;
  accountId: string;
  date: string;
  lines: RefundLineState[];
  /** true, wenn alle Ursprungspools vollständig erstattet sind. */
  fullyRefunded: boolean;
  error: ApiErrorInfo | null;
}

/**
 * Belege (7a): zulässige Content-Types (PDF, JPEG, PNG — Design D7).
 * Es gibt bewusst KEINE unabhängige Client-Größensperre (Review 7a,
 * Befund „Serverlimit“): das konfigurierbare Server-Limit
 * (`Lario:Attachments:MaxBytes`, Default 20.000.000 Byte) bleibt
 * maßgeblich — ein über das Client-Default hinausgehendes Server-Limit
 * muss über die UI nutzbar sein. Zu große Dateien werden serverseitig
 * abgewiesen (Byte-Zähler / Multipart- / Kestrel-Limit) und die 413-
 * Meldung ist im Dialog verständlich, danach bleibt eine kleinere
 * Datei auswählbar.
 */
const ATTACHMENT_TYPES = new Set(['application/pdf', 'image/jpeg', 'image/png']);

const TYPE_LABELS: Record<string, string> = {
  Income: 'Einnahme',
  Expense: 'Ausgabe',
  AccountTransfer: 'Kontotransfer',
  Reassignment: 'Umwidmung',
  TransferReassignment: 'Transfer + Umwidmung',
  Reservation: 'Reservierung',
  Closeout: 'Abschluss',
  Reversal: 'Storno',
  Refund: 'Erstattung',
  BalanceCorrection: 'Korrektur Kontostand',
  OpeningBalance: 'Anfangsbestand',
};

export interface AttachmentDialogState {
  booking: BookingListItemDto;
  /** Dialog-Generation: wird bei jedem Öffnen erhöht. Alle asynchronen
   *  Antworten werden nur auf die Generation angewendet, in der sie
   *  gestartet wurden — eine verzögerte Antwort eines älteren Dialogs
   *  (nach Dialogwechsel oder -schließung) ist ein No-op und kann
   *  Detail, Liste, Vorschau oder Erfolgs-/Fehlerzustand des aktuellen
   *  Dialogs nicht überschreiben. */
  generation: number;
  /** Lade Zustand: Buchungsdetail + Belegliste. */
  loading: boolean;
  /** Schreibaktion läuft (Hochladen, Entfernen, Vorschau laden). */
  busy: boolean;
  detail: BookingDetailDto | null;
  attachments: AttachmentDto[];
  selectedFile: File | null;
  /** Leer = gesamte Ausgabe; sonst der gewählte Split (nur bei >1 Splits). */
  splitId: string;
  /** Client-Vorvalidierung (Typ/Große) am gewählten File. */
  clientError: string | null;
  error: ApiErrorInfo | null;
  /** Erfolgszustand des letzten Uploads — bleibt auch dann sichtbar,
   *  wenn der anschließende Listen-Refresh fehlschlägt, damit der
   *  bestätigte Upload erkennbar bleibt (die Dateiauswahl wird
   *  zurückgesetzt, sodass kein unbedachter erneuter Upload derselben
   *  Datei möglich ist). */
  success: string | null;
  /** Beleg, für den gerade ein Preview-/Download-Blob geladen wird. */
  busyAttachmentId: string | null;
  /** Beleg, dessen Bildvorschau angezeigt wird (nur Bilder). */
  previewId: string | null;
  /** Blob-URL der angezeigten Vorschau (gehört zu `previewId`). */
  previewUrl: string | null;
  /** Beleg, für den gerade die Löschbestätigung angezeigt wird. */
  confirmDeleteId: string | null;
}

const FILTER_TYPES = [
  { value: 'all', label: 'Alle Typen' },
  { value: 'Expense', label: 'Ausgaben' },
  { value: 'Income', label: 'Einnahmen' },
  { value: 'AccountTransfer', label: 'Kontotransfers' },
  { value: 'Reassignment', label: 'Umwidmungen' },
  { value: 'TransferReassignment', label: 'Transfer + Umwidmung' },
  { value: 'Reversal', label: 'Stornos' },
  { value: 'Refund', label: 'Erstattungen' },
  { value: 'OpeningBalance', label: 'Anfangsbestände' },
  { value: 'BalanceCorrection', label: 'Korrektur Kontostand' },
];

function newOperationId(prefix: string): string {
  const random =
    typeof crypto !== 'undefined' && 'randomUUID' in crypto
      ? crypto.randomUUID()
      : Math.random().toString(36).slice(2);
  return `${prefix}-${random}`;
}

/**
 * Erstattungszeilen aus dem Buchungsdetail ableiten (Review R3 P2):
 *
 * - Aggregation pro Pool: eine Erstattung fasst einen Pool in einer
 *   Zeile auf (serverseitige Regel); mehrere Splits desselben Pools
 *   zählen zusammen zur Ursprungs-Position.
 * - Verbleibender Deckel pro Pool = Split-Position minus bereits
 *   gewährter Erstattungen ({@link BookingDetailDto.refundedByPool}) —
 *   nach einer Teilerstattung ist nur noch der Rest erstattungsfähig.
 * - Vollständig erstattete Pools fallen aus den Zeilen heraus;
 *   sind alle Pools vollständig erstattet, ist die Ausgabe
 *   {@link fullyRefunded}.
 */
export function refundLinesFromDetail(detail: BookingDetailDto): {
  lines: RefundLineState[];
  fullyRefunded: boolean;
} {
  const positionByPool = new Map<string, number>();
  const nameByPool = new Map<string, string | null>();
  for (const split of detail.splits) {
    if (!split.poolId) {
      continue;
    }
    positionByPool.set(
      split.poolId,
      (positionByPool.get(split.poolId) ?? 0) + Number(split.amount),
    );
    if (!nameByPool.has(split.poolId)) {
      nameByPool.set(split.poolId, split.poolName);
    }
  }

  const refundedByPool = new Map<string, number>();
  for (const entry of detail.refundedByPool) {
    refundedByPool.set(
      entry.poolId,
      (refundedByPool.get(entry.poolId) ?? 0) + Number(entry.amount),
    );
  }

  const lines: RefundLineState[] = [];
  for (const [poolId, position] of positionByPool) {
    const refunded = Math.min(refundedByPool.get(poolId) ?? 0, position);
    const remaining = position - refunded;
    if (remaining > 0) {
      lines.push({
        poolId,
        poolName: nameByPool.get(poolId) ?? null,
        maxCents: remaining,
        refundedCents: refunded,
        amountText: '',
      });
    }
  }

  return { lines, fullyRefunded: positionByPool.size > 0 && lines.length === 0 };
}

@Component({
  selector: 'app-bookings-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BalancesPanel],
  templateUrl: './bookings-list.html',
  styleUrl: './bookings-list.css',
})
export class BookingsList implements OnInit, OnDestroy {
  readonly typeLabels = TYPE_LABELS;
  readonly filterTypes = FILTER_TYPES;
  readonly todayIso = todayIso;

  typeFilter = 'Expense';
  fromFilter = '';
  toFilter = '';

  readonly items = signal<BookingListItemDto[]>([]);
  readonly loading = signal(false);
  readonly loadError = signal<ApiErrorInfo | null>(null);
  readonly success = signal<string | null>(null);
  /**
   * Aktuelle Salden (Zwischenstep 7.5, A1): Konten- und Pool-Salden
   * über der Buchungsliste. Ergänzende Ansicht — ein Fehler beim Laden
   * der Salden blockiert die Liste nicht (dann ohne Karte).
   */
  readonly balances = signal<BalancesDto | null>(null);

  readonly reversal = signal<ReversalDialogState | null>(null);
  readonly refund = signal<RefundDialogState | null>(null);
  readonly attachments = signal<AttachmentDialogState | null>(null);

  /** Zählt die Dialog-Generationen (siehe AttachmentDialogState). */
  private attachmentGeneration = 0;

  private readonly router = inject(Router);
  readonly catalog = inject(CatalogService);
  readonly errors = inject(ApiErrorHandler);

  ngOnDestroy(): void {
    // Beleg-Vorschau (Blob-URL) bei Zerstörung der Komponente freigeben
    // (Dialogwechsel/-schließung räumen in closeAttachments auf — hier
    // für den Fall, dass der Dialog noch offen ist, z. B. Navigation
    // weg von der Buchungsliste).
    const url = this.attachments()?.previewUrl;
    if (url) {
      URL.revokeObjectURL(url);
    }
  }

  async ngOnInit(): Promise<void> {
    await this.catalog.load();
    await Promise.all([this.load(), this.loadBalances()]);
  }

  async load(): Promise<void> {
    if (this.loading()) {
      return;
    }
    this.loading.set(true);
    this.loadError.set(null);
    this.success.set(null);
    try {
      const items = await JournalEndpointsService.listBookings(
        this.typeFilter === 'all' ? undefined : this.typeFilter,
        this.fromFilter || undefined,
        this.toFilter || undefined,
        500,
      );
      this.items.set(items);
    } catch (error) {
      this.loadError.set(this.errors.handle(error));
    } finally {
      this.loading.set(false);
    }
  }

  /** Salden laden (A1); Fehler bleiben lokal — die Liste funktioniert ohne. */
  private async loadBalances(): Promise<void> {
    try {
      this.balances.set(await JournalEndpointsService.getBalances());
    } catch {
      this.balances.set(null);
    }
  }

  /** Salden aktualisieren (nach Schreibaktionen, z. B. Storno). */
  async refreshBalances(): Promise<void> {
    await this.loadBalances();
  }

  typeLabel(type: string): string {
    return TYPE_LABELS[type] ?? type;
  }

  /**
   * Buchungen, für die der UI-Korrektur-Button sichtbar ist: nur Typen mit
   * vorhandenem Korrekturformular — Ausgabe (ausgaben/:id/korrektur) und
   * Anfangsbestand (anfangsbestaende/:id/korrektur). Die Domain definiert
   * Korrektur zusätzlich für Einnahmen (CorrectionService), dafür existiert
   * in der UI aber noch kein Einnahmenformular — bis dahin ändern
   * Einnahmen über Storno + Neuausgabe (der Storno-Button bleibt für sie
   * verfügbar). Gleiches gilt für alle übrigen Typen (Storno + Neu).
   */
  isCorrectable(item: BookingListItemDto): boolean {
    return item.type === 'Expense' || item.type === 'OpeningBalance';
  }

  /**
   * Typgerechte Korrektur-Route öffnen (7.5, offener Punkt): ein
   * Anfangsbestand öffnet das Anfangsbestände-Korrekturformular
   * (Verteilung auf Pools, in-place), Ausgaben das
   * Ausgaben-Korrekturformular. isCorrectable() begrenzt den Button auf
   * genau diese beiden Typen — die Ausgaben-Branch erreicht nur Ausgaben.
   * Beide Pfade sind absolut (relative Navigation löst gegen die Wurzel
   * auf und trifft die falsche Route).
   */
  goCorrection(item: BookingListItemDto): void {
    const base =
      item.type === 'OpeningBalance' ? '/anfangsbestaende' : '/ausgaben';
    void this.router.navigate([base, item.id, 'korrektur']);
  }

  // ---------- Storno ----------

  openReversal(booking: BookingListItemDto): void {
    this.reversal.set({
      booking,
      operationId: newOperationId('rev'),
      date: todayIso(),
      note: '',
      busy: false,
      error: null,
    });
  }

  closeReversal(): void {
    this.reversal.set(null);
  }

  async submitReversal(): Promise<void> {
    const dialog = this.reversal();
    if (!dialog || dialog.busy || !dialog.date) {
      return;
    }
    this.patchReversal({ busy: true, error: null });
    try {
      await JournalWriteEndpointsService.createReversal(dialog.booking.id, {
        id: dialog.operationId,
        expectedBaseRevision: Number(dialog.booking.baseRevision),
        date: dialog.date,
        note: dialog.note.trim() === '' ? null : dialog.note.trim(),
      });
      this.reversal.set(null);
      await this.load();
      await this.refreshBalances();
      // load() setzt success auf null — daher erst danach die Meldung setzen.
      this.success.set(`Storno zu ${formatDate(dialog.booking.date)} gebucht.`);
    } catch (error) {
      this.patchReversal({ busy: false, error: this.errors.handle(error) });
    }
  }

  private patchReversal(patch: Partial<ReversalDialogState>): void {
    this.reversal.update((d) => (d ? { ...d, ...patch } : d));
  }

  // ---------- Erstattung ----------

  openRefund(booking: BookingListItemDto): void {
    const state: RefundDialogState = {
      booking,
      operationId: newOperationId('refund'),
      loading: true,
      busy: false,
      detail: null,
      accountId: booking.accountId ?? '',
      date: todayIso(),
      lines: [],
      fullyRefunded: false,
      error: null,
    };
    this.refund.set(state);
    void this.loadRefundDetail(state);
  }

  private async loadRefundDetail(state: RefundDialogState): Promise<void> {
    try {
      const detail = await JournalEndpointsService.getBooking(state.booking.id);
      const { lines, fullyRefunded } = refundLinesFromDetail(detail);
      this.patchRefund(state, {
        loading: false,
        detail,
        accountId: state.accountId || detail.accountPostings[0]?.accountId || '',
        lines,
        fullyRefunded,
      });
    } catch (error) {
      this.patchRefund(state, { loading: false, error: this.errors.handle(error) });
    }
  }

  closeRefund(): void {
    this.refund.set(null);
  }

  refundSumCents(): number {
    return (
      this.refund()?.lines.reduce(
        (sum, line) => sum + (parseEuroToCents(line.amountText) ?? 0),
        0,
      ) ?? 0
    );
  }

  refundValid(): boolean {
    const dialog = this.refund();
    if (!dialog || !dialog.detail || !dialog.accountId || !dialog.date) {
      return false;
    }
    // Review R4 F3: leer/0-Zeilen sind nicht ausgewählt (keine
    // Teil-Erstattung blockieren); mindestens eine Zeile mit positivem
    // Betrag erforderlich; ungültige nicht-leere Einträge und
    // Überschreitung des Deckels bleiben abgelehnt.
    let hasPositive = false;
    for (const line of dialog.lines) {
      const amount = parseEuroToCents(line.amountText);
      if (amount === null) {
        if (line.amountText.trim() !== '') {
          return false; // ungültiger Eintrag
        }
        continue; // leer = nicht ausgewählt
      }
      if (amount === 0) {
        continue; // 0 = nicht ausgewählt
      }
      if (amount > line.maxCents) {
        return false;
      }
      hasPositive = true;
    }
    return hasPositive;
  }

  async submitRefund(): Promise<void> {
    const dialog = this.refund();
    if (!dialog || dialog.busy || !dialog.detail || !this.refundValid()) {
      return;
    }
    this.patchRefund(dialog, { busy: true, error: null });
    try {
      await JournalWriteEndpointsService.createRefund(dialog.booking.id, {
        id: dialog.operationId,
        accountId: dialog.accountId,
        date: dialog.date,
        lines: dialog.lines
          .map((line) => ({
            poolId: line.poolId,
            // Review R4 F3: leer/0 = nicht ausgewählt → nicht übertragen.
            amount: parseEuroToCents(line.amountText) ?? 0,
          }))
          .filter((line) => line.amount > 0),
        note: null,
      });
      this.refund.set(null);
      await this.load();
      await this.refreshBalances();
      // load() setzt success auf null — daher erst danach die Meldung setzen.
      this.success.set(`Erstattung zu ${formatDate(dialog.booking.date)} gebucht.`);
    } catch (error) {
      this.patchRefund(dialog, { busy: false, error: this.errors.handle(error) });
    }
  }

  private patchRefund(state: RefundDialogState, patch: Partial<RefundDialogState>): void {
    this.refund.update((d) => (d && d.booking.id === state.booking.id ? { ...d, ...patch } : d));
  }

  // ---------- Belege (7a) ----------

  openAttachments(booking: BookingListItemDto): void {
    const generation = ++this.attachmentGeneration;
    const state: AttachmentDialogState = {
      booking,
      generation,
      loading: true,
      busy: false,
      detail: null,
      attachments: [],
      selectedFile: null,
      splitId: '',
      clientError: null,
      error: null,
      success: null,
      busyAttachmentId: null,
      previewId: null,
      previewUrl: null,
      confirmDeleteId: null,
    };
    this.attachments.set(state);
    void this.loadAttachmentDialog(generation, booking.id, '');
  }

  closeAttachments(): void {
    const url = this.attachments()?.previewUrl;
    if (url) {
      URL.revokeObjectURL(url);
    }
    this.attachments.set(null);
  }

  /** Patch nur für den Dialog der übergebenen Generation — verzögerte
   *  Antworten eines geschlossenen oder gewechselten Dialogs sind damit
   *  automatisch No-ops (Review-Befind: Dialog-Generation). */
  private patchAttachmentDialog(
    generation: number,
    patch: Partial<AttachmentDialogState>,
  ): void {
    this.attachments.update((d) => (d?.generation === generation ? { ...d, ...patch } : d));
  }

  /** Patch für den aktuell angezeigten Dialog — nur für synchrone
   *  Nutzeraktionen ohne laufende asynchrone Antwort (z. B. Lösch-
   *  bestätigung ein-/ausblenden). */
  private patchCurrentAttachment(patch: Partial<AttachmentDialogState>): void {
    this.attachments.update((d) => (d ? { ...d, ...patch } : d));
  }

  private async loadAttachmentDialog(
    generation: number,
    bookingId: string,
    splitId: string,
  ): Promise<void> {
    try {
      const [detail, list] = await Promise.all([
        JournalEndpointsService.getBooking(bookingId),
        AttachmentEndpointsService.getBookingAttachments(bookingId),
      ]);
      this.patchAttachmentDialog(generation, {
        loading: false,
        detail,
        attachments: list,
        // Split-Zuordnung nur wählbar bei mehreren Splits (R7a-2);
        // ansonsten gehört der Beleg zur gesamten Ausgabe.
        splitId: detail.splits.length > 1 ? splitId : '',
        // Eine frisch geladene Liste ersetzt den Erfolgshinweis (der neue
        // Beleg ist dann selbst in der Liste sichtbar).
        success: null,
      });
    } catch (error) {
      this.patchAttachmentDialog(generation, {
        loading: false,
        error: this.errors.handle(error),
      });
    }
  }

  /** Retry für das initiale Laden (Ladefehler → Detail fehlt): dieselbe
   *  Generation, Fehlerhinweis wird zurückgesetzt und die Daten werden
   *  erneut geladen. Bei einem Aktionsfehler ist der Retry nicht nötig —
   *  der geladene Dialoginhalt (Liste, Eingaben, Aktionen) bleibt
   *  bedienbar. */
  retryAttachmentDialog(): void {
    const dialog = this.attachments();
    if (!dialog || dialog.loading || dialog.detail) {
      return;
    }
    this.patchAttachmentDialog(dialog.generation, {
      loading: true,
      error: null,
    });
    void this.loadAttachmentDialog(
      dialog.generation,
      dialog.booking.id,
      dialog.splitId,
    );
  }

  /** Client-Vorvalidierung (D8): nur der Dateityp wird lokal geprüft;
   *  die Größe entscheidet der Server (siehe ATTACHMENT_TYPES-Kommentar). */
  private validateFile(file: File): string | null {
    if (!ATTACHMENT_TYPES.has(file.type)) {
      return 'Erlaubt sind nur PDF-, JPEG- und PNG-Dateien.';
    }
    return null;
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    input.value = '';
    this.attachments.update((d) =>
      d
        ? { ...d, selectedFile: file, clientError: file ? this.validateFile(file) : null }
        : d,
    );
  }

  canUpload(): boolean {
    const dialog = this.attachments();
    return (
      !!dialog &&
      !dialog.loading &&
      !dialog.busy &&
      dialog.selectedFile !== null &&
      dialog.clientError === null
    );
  }

  async uploadAttachment(): Promise<void> {
    const dialog = this.attachments();
    const file = dialog?.selectedFile;
    if (!dialog || !file || dialog.busy || this.validateFile(file) !== null) {
      return;
    }
    const generation = dialog.generation;
    this.patchAttachmentDialog(generation, { busy: true, error: null, success: null });
    try {
      // `File` ist ein `Blob` (generierter Typ IFormFile = Blob); als File
      // übergeben bleibt der Original-Dateiname im Multipart-Body erhalten.
      await AttachmentEndpointsService.uploadAttachment({
        file,
        bookingId: dialog.booking.id,
        splitId: dialog.splitId || undefined,
      });
      // Upload ist bestätigt — der Erfolgshinweis bleibt auch sichtbar,
      // wenn der anschließende Refresh fehlschlägt (Dateiauswahl ist
      // zurückgesetzt, d. h. kein unbedachter erneuter Upload derselben
      // Datei).
      this.patchAttachmentDialog(generation, {
        busy: false,
        selectedFile: null,
        clientError: null,
        error: null,
        success: 'Beleg hochgeladen.',
      });
      await this.loadAttachmentDialog(generation, dialog.booking.id, dialog.splitId);
    } catch (error) {
      this.patchAttachmentDialog(generation, { busy: false, error: this.errors.handle(error) });
    }
  }

  isImageAttachment(attachment: AttachmentDto): boolean {
    return attachment.contentType.startsWith('image/');
  }

  isPdfAttachment(attachment: AttachmentDto): boolean {
    return attachment.contentType.startsWith('application/pdf');
  }

  /** Zuordnung eines Belegs zum Split als Anzeige-Label. */
  attachmentSplitLabel(attachment: AttachmentDto): string {
    if (attachment.splitId === null) {
      return 'Gesamte Ausgabe';
    }
    const split = this.attachments()?.detail?.splits.find((s) => s.id === attachment.splitId);
    return split ? `${split.poolName ?? 'Split'} (${formatCents(split.amount)})` : 'Split';
  }

  /** Bildvorschau über Blob-URL (Design D4). */
  async showPreview(attachment: AttachmentDto): Promise<void> {
    const dialog = this.attachments();
    if (!dialog || !this.isImageAttachment(attachment)) {
      return;
    }
    const generation = dialog.generation;
    this.patchAttachmentDialog(generation, { busyAttachmentId: attachment.id, error: null });
    try {
      const blob = await fetchAttachmentBlob(attachment.id);
      // Verzögerte Antwort für einen älteren Dialog (bereits gewechselt
      // oder geschlossen): keine Vorschau setzen, keine neue URL anlegen.
      if (this.attachments()?.generation !== generation) {
        return;
      }
      const url = URL.createObjectURL(blob);
      this.attachments.update((d) => {
        if (d?.generation !== generation) {
          return d;
        }
        if (d.previewUrl && d.previewUrl !== url) {
          URL.revokeObjectURL(d.previewUrl);
        }
        return { ...d, busyAttachmentId: null, previewId: attachment.id, previewUrl: url };
      });
    } catch (error) {
      if (this.attachments()?.generation !== generation) {
        return;
      }
      this.patchAttachmentDialog(generation, {
        busyAttachmentId: null,
        error: this.errors.handle(error),
      });
    }
  }

  /** PDF im neuen Tab öffnen (Blob-URL; wird bewusst nicht revokiert,
   *  weil der neue Tab sie noch braucht). */
  async openAttachment(attachment: AttachmentDto): Promise<void> {
    const dialog = this.attachments();
    if (!dialog || !this.isPdfAttachment(attachment)) {
      return;
    }
    const generation = dialog.generation;
    this.patchAttachmentDialog(generation, { busyAttachmentId: attachment.id, error: null });
    try {
      const blob = await fetchAttachmentBlob(attachment.id);
      if (this.attachments()?.generation !== generation) {
        return;
      }
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank', 'noopener');
      this.patchAttachmentDialog(generation, { busyAttachmentId: null });
    } catch (error) {
      if (this.attachments()?.generation !== generation) {
        return;
      }
      this.patchAttachmentDialog(generation, {
        busyAttachmentId: null,
        error: this.errors.handle(error),
      });
    }
  }

  /** Download über temporäre <a download>-Brücke. */
  async downloadAttachment(attachment: AttachmentDto): Promise<void> {
    const dialog = this.attachments();
    if (!dialog) {
      return;
    }
    const generation = dialog.generation;
    this.patchAttachmentDialog(generation, { busyAttachmentId: attachment.id, error: null });
    try {
      const blob = await fetchAttachmentBlob(attachment.id);
      if (this.attachments()?.generation !== generation) {
        return;
      }
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = attachment.originalFilename || `beleg-${attachment.id.slice(0, 8)}`;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
      this.patchAttachmentDialog(generation, { busyAttachmentId: null });
    } catch (error) {
      if (this.attachments()?.generation !== generation) {
        return;
      }
      this.patchAttachmentDialog(generation, {
        busyAttachmentId: null,
        error: this.errors.handle(error),
      });
    }
  }

  requestDeleteAttachment(attachment: AttachmentDto): void {
    this.patchCurrentAttachment({ confirmDeleteId: attachment.id, error: null });
  }

  cancelDeleteAttachment(): void {
    this.patchCurrentAttachment({ confirmDeleteId: null });
  }

  async confirmDeleteAttachment(): Promise<void> {
    const dialog = this.attachments();
    const id = dialog?.confirmDeleteId;
    if (!dialog || !id || dialog.busy) {
      return;
    }
    const generation = dialog.generation;
    this.patchAttachmentDialog(generation, { busy: true, error: null });
    try {
      await AttachmentEndpointsService.deleteAttachment(id);
      this.attachments.update((d) => {
        if (d?.generation !== generation) {
          return d;
        }
        // Gelöschter Beleg war die angezeigte Vorschau → Vorschau aufräumen.
        const previewCleared = d.previewId === id;
        if (previewCleared && d.previewUrl) {
          URL.revokeObjectURL(d.previewUrl);
        }
        return {
          ...d,
          busy: false,
          confirmDeleteId: null,
          previewId: previewCleared ? null : d.previewId,
          previewUrl: previewCleared ? null : d.previewUrl,
        };
      });
      await this.loadAttachmentDialog(generation, dialog.booking.id, dialog.splitId);
    } catch (error) {
      this.patchAttachmentDialog(generation, { busy: false, error: this.errors.handle(error) });
    }
  }

  // ---------- Anzeige ----------

  formatBytes = formatBytes;
  formatCents = formatCents;
  formatDate = formatDate;
}
