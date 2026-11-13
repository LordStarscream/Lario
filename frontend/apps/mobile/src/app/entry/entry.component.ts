// Batch D (P4) — Erfassen-Komponente: Ausgabe erfassen (primäre
// Startansicht). Eingabe und Outbox in derselben lokalen
// SQLite-Transaktion; die UI bestätigt erst nach Commit
// (BookingsRepository.save, Batch B).
//
// Zoneless: die App läuft ohne zone.js (Angular-22-Default,
// main.ts ohne Zone-Provider); die Komponente nutzt OnPush +
// reine Signale.
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { formatCents, parseAmount } from '../amount';
import { BookingsRepository, BookingValidationError } from '../db/bookings.repository';
import { DraftsRepository } from '../db/drafts.repository';
import {
  SnapshotRepository,
  type MasterDataSnapshot,
} from '../db/snapshot.repository';
import {
  applyEqualSplit,
  applyTaxPreset,
  createDefaultForm,
  createDefaultSplit,
  fillLastSplit,
  parseDraft,
  serializeDraft,
  setTaxRelevant,
  sumCentsSafe,
  fromBookingDetail,
  toLocalInput,
  todayInBerlin,
  validateEntryForm,
  type EntryFormError,
  type EntryFormModel,
  type EntrySplitModel,
  type EntryTaxModel,
} from './entry-form.model';

@Component({
  selector: 'app-entry',
  templateUrl: './entry.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
})
export class EntryComponent implements OnInit {
  private readonly bookingsRepository = inject(BookingsRepository);
  private readonly draftsRepository = inject(DraftsRepository);
  private readonly snapshotRepository = inject(SnapshotRepository);

  /** Formularzustand (Signal). */
  readonly form = signal<EntryFormModel>(createDefaultForm(todayInBerlin()));
  /** Stammdaten-Snapshot (null = noch nicht geladen). */
  readonly snapshot = signal<MasterDataSnapshot | null>(null);
  /** Fehlermeldung des Snapshot-Loads. */
  readonly snapshotError = signal<string | null>(null);
  /** true = Transaktion läuft (Sperre gegen Doppeltippen). */
  readonly saving = signal(false);
  /** Speichern-Status: idle = noch kein Versuch, saved = erfolgreich,
   *  info = Hinweis (z. B. Abbrechen, kein Entwurf vorhanden),
   *  error = fehlgeschlagen. */
  readonly saveStatus = signal<'idle' | 'saved' | 'error' | 'info'>('idle');
  readonly saveMessage = signal<string | null>(null);

  /**
   * ID der aktuell bearbeiteten, in der Outbox wartenden Buchung
   * (Batch E, Design D10): `null` = neue Ausgabe erfassen.
   * Der Ändern-Modus wird über die Route `erfassen/:bookingId`
   * aktiviert; während der Bearbeitung sind die Entwurf-Buttons
   * deaktiviert (das Formular gehört zur Bearbeitung, nicht zum
   * Entwurf).
   */
  readonly editingBookingId = signal<string | null>(null);

  /**
   * Banner-Text des Ändern-Modus (Datum + Gesamtbetrag der
   * Originalbuchung), gesetzt in `enterEdit`, geleert in
   * `clearEditMode`.
   */
  readonly editBannerText = signal<string | null>(null);

  private readonly route = inject(ActivatedRoute);

  /**
   * Nicht übernahmener Entwurf vorhanden (Design D8): `true` beim
   * App-Start-Check (`ngOnInit`) und nach `saveDraft`; `false` nach
   * erfolgreichem `loadDraft`/`discardDraft`. Die Übernahme bleibt
   * eine bewusste Aktion („Entwurf laden“).
   */
  readonly draftLoaded = signal(false);

  /** Formularfehler (computed). */
  readonly errors = computed<EntryFormError[]>(() =>
    validateEntryForm(this.form(), this.snapshot()),
  );

  /** Gesamtbetrag in Cent (null = ungültig/leer). */
  readonly totalCents = computed<number | null>(() => {
    const c = parseAmount(this.form().amountInput);
    return c === null || c === undefined ? null : c;
  });

  /** Summe der gültigen Split-Beträge in Cent (null = außerhalb des
   *  sicheren Bereichs, Batch D Review, P3 — die Live-Anzeige darf
   *  nicht werfen).
   */
  readonly splitSum = computed<number | null>(() => {
    const cents: number[] = [];
    for (const s of this.form().splits) {
      const c = parseAmount(s.amountInput);
      if (c !== null && c !== undefined) {
        cents.push(c);
      }
    }
    return sumCentsSafe(cents);
  });

  /** Abweichung Split-Summe − Gesamt (null = Gesamt oder Summe
   *  ungültig).
   */
  readonly deviationCents = computed<number | null>(() => {
    const total = this.totalCents();
    const sum = this.splitSum();
    return total === null || sum === null ? null : sum - total;
  });

  /** Konto-Optionen (aktiv, nach Name sortiert). */
  readonly accountOptions = computed(() =>
    this.snapshotOptions(this.snapshot()?.accounts),
  );

  /** Pool-Optionen (aktiv, nach Name sortiert). */
  readonly poolOptions = computed(() =>
    this.snapshotOptions(this.snapshot()?.pools),
  );

  /** Kategorie-Optionen (aktiv, nach Name sortiert). */
  readonly categoryOptions = computed(() =>
    this.snapshotOptions(this.snapshot()?.categories),
  );

  /** Steuerkategorie-Optionen (aktiv, nach Name sortiert). */
  readonly taxCategoryOptions = computed(() =>
    this.snapshotOptions(this.snapshot()?.taxCategories),
  );

  /** Quick-Actions: gültiger Gesamtbetrag > 0 und mindestens ein Split
   *  (beide Buttons, Design D8).
   */
  readonly quickActionsEnabled = computed(() => {
    const total = this.totalCents();
    return (
      total !== null && total > 0 && this.form().splits.length > 0
    );
  });

  /** Speichern-Sperre: während Transaktion oder ohne Snapshot. */
  readonly saveEnabled = computed(
    () => !this.saving() && this.snapshot() !== null,
  );

  /**
   * Generationszähler des Formularzustands (Batch D Review, P2):
   * jede Mutation erhöht ihn — einschließlich des erfolgreichen
   * Formular-Reset nach dem Save (deshalb erkennt ein vor dem Save
   * gestarteter Entwurf-Load mit verspäteter Antwort den abgeschlossenen
   * Save). `save()` erfasst den Zähler vor dem Repository-Aufruf und
   * setzt das Formular nach dem Commit nur zurück, wenn sich nichts
   * geändert hat — eine zwischenzeitliche Änderung wird nie still
   * gelöscht.
   */
  private formGeneration = 0;

  /** Lädt den Stammdaten-Snapshot (Design D9; Fehlfall → Hinweis, das
   *  Formular bleibt bedienbar, Speichern ist gesperrt) und aktiviert
   *  den Ändern-Modus, wenn die Route ein `bookingId`-Parameter trägt
   *  (Batch E, P5: `erfassen/:bookingId` — Deep-Links und Browser-
   *  Back-Button funktionieren über den Router).
   */
  async ngOnInit(): Promise<void> {
    try {
      this.snapshot.set(await this.snapshotRepository.loadSnapshot());
    } catch (e) {
      this.snapshotError.set(`Snapshot konnte nicht geladen werden: ${describeError(e)}`);
    }
    // App-Start-Check (Design D8): ein vorhandener Entwurf wird
    // sichtbar (Hinweis + `draftLoaded`); die Übernahme bleibt eine
    // bewusste Aktion. Kein Startblocker: ein Check-Fehler bleibt
    // still (DB-Fehler meldet das Speichern trotzdem).
    try {
      const draft = await this.draftsRepository.get(DRAFT_ID);
      if (draft !== null) {
        this.draftLoaded.set(true);
        if (this.saveStatus() === 'idle') {
          this.saveStatus.set('info');
          this.saveMessage.set(
            'Entwurf vorhanden — mit „Entwurf laden“ übernehmen.',
          );
        }
      }
    } catch {
      this.draftLoaded.set(false);
    }
    const bookingId = this.route.snapshot.paramMap.get('bookingId');
    if (bookingId !== null) {
      await this.enterEdit(bookingId);
    }
  }

  // --- Formular-Mutationen -------------------------------------------------

  /**
   * Zentrale Mutation des Formularzustands (Batch D Review, P2):
   * während der Save-Transaktion (`saving`) werden alle
   * formularverändernden Operationen blockiert; jede Mutation
   * erhöht den Generationszähler.
   */
  private setForm(model: EntryFormModel): void {
    if (this.saving()) {
      return;
    }
    this.formGeneration += 1;
    this.form.set(model);
  }

  /** Patcht den Formularzustand (s. `setForm`). */
  private mutateForm(fn: (f: EntryFormModel) => EntryFormModel): void {
    this.setForm(fn(this.form()));
  }

  /** Aktualisiert ein Top-Level-Feld des Formulars (Datum, Betrag,
   *  Konto, Notiz).
   */
  updateField(patch: Partial<Pick<EntryFormModel, 'date' | 'amountInput' | 'accountId' | 'note'>>): void {
    this.mutateForm((f) => ({ ...f, ...patch }));
  }

  /** Aktualisiert ein Feld eines Splits (Betrag, Pool, Kategorie,
   *  Notiz).
   */
  updateSplit(key: string, patch: Partial<Omit<EntrySplitModel, 'tax' | 'key' | 'id'>>): void {
    this.mutateForm((f) => ({
      ...f,
      splits: f.splits.map((s) => (s.key === key ? { ...s, ...patch } : s)),
    }));
  }

  /** Aktualisiert die Steuerkennzeichnung eines Splits (Kategorie,
   *  markierter Betrag, Notiz).
   */
  updateSplitTax(key: string, taxPatch: Partial<EntryTaxModel>): void {
    this.mutateForm((f) => ({
      ...f,
      splits: f.splits.map((s) =>
        s.key === key ? { ...s, tax: { ...s.tax, ...taxPatch } } : s,
      ),
    }));
  }

  /** Setzt die Steuerkategorie eines Splits; der Anzeigename wird
   *  aus dem Snapshot abgeleitet (Batch D Review, P2). Der
   *  persistierte Name wird von `toLocalInput` zusätzlich aus dem
   *  Snapshot neu abgeleitet (maßgeblich).
   */
  onSplitTaxCategoryChange(key: string, taxCategoryId: string | null): void {
    const snapshot = this.snapshot();
    const name =
      taxCategoryId === null
        ? null
        : snapshot?.taxCategories.get(taxCategoryId)?.name ?? null;
    this.mutateForm((f) => ({
      ...f,
      splits: f.splits.map((s) =>
        s.key === key
          ? { ...s, tax: { ...s.tax, taxCategoryId, taxCategoryName: name } }
          : s,
      ),
    }));
  }

  /** Setzt den Steuer-Schalter eines Splits; beim Umschalten auf
   *  "nicht relevant" werden Steuerkategorie und markierter Betrag
   *  geleert (Design D7).
   */
  onSplitTaxRelevantChange(key: string, relevant: boolean): void {
    this.mutateForm((f) => setTaxRelevant(f, key, relevant));
  }

  /** Setzt den Pool eines Splits. */
  onSplitPoolChange(key: string, poolId: string): void {
    this.updateSplit(key, { poolId });
  }

  /** Setzt die Kategorie eines Splits und übernimmt den
   *  `taxPreset` (Design D7).
   */
  onSplitCategoryChange(key: string, categoryId: string): void {
    const snapshot = this.snapshot();
    const category = categoryId === '' ? undefined : snapshot?.categories.get(categoryId);
    this.mutateForm((f) => ({
      ...f,
      splits: f.splits.map((s) =>
        s.key === key
          ? applyTaxPreset({ ...s, categoryId }, category?.taxPreset ?? null)
          : s,
      ),
    }));
  }

  /** Fügt einen Split hinzu (Position: am Ende, Key "sN"). */
  addSplit(): void {
    this.mutateForm((f) => {
      const nextKey = `s${f.splits.length + 1}`;
      return { ...f, splits: [...f.splits, createDefaultSplit(nextKey)] };
    });
  }

  /** Entfernt den Split mit dem Key (mindestens 1 Split bleibt
   *  erlaubt — die Validierung meldet "missing_split").
   */
  removeSplit(key: string): void {
    this.mutateForm((f) => {
      const remaining = f.splits.filter((s) => s.key !== key);
      // Keys neu nummerieren (s1, s2, ...), damit die Position
      // stabil bleibt (Design D4).
      return {
        ...f,
        splits: remaining.map((s, i) => ({ ...s, key: `s${i + 1}` })),
      };
    });
  }

  /** Quick-Action "Gleichmäßig" (Design D8). */
  quickEvenly(): void {
    const result = applyEqualSplit(this.form());
    if (result !== null) {
      this.setForm(result);
    }
  }

  /** Quick-Action "Rest in letzten Split" (Design D8). */
  quickFillLast(): void {
    this.setForm(fillLastSplit(this.form()));
  }

  // --- Speichern ------------------------------------------------------------

  /**
   * Speichert die Ausgabe (eine Transaktion: Buchung + Splits +
   * Outbox, Batch B). Neu erfassen: `save` (neue `bookingId`) +
   * „Gespeichert in der Outbox.“. Ändern: `save(bookingId, ...)`
   * (derselbe `operationId`/`payloadHash` — keine zweite Outbox-
   * Zeile, Batch B) + „Änderungen gespeichert.“. Erst nach Commit
   * wird bestätigt; bei Validierungs-/DB-Fehlern bleibt das
   * Formular erhalten.
   */
  async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    if (this.errors().length > 0) {
      this.saveStatus.set('error');
      this.saveMessage.set('Bitte Eingaben korrigieren (Fehler unten).');
      return;
    }
    const input = toLocalInput(this.form(), this.snapshot());
    if (input === null) {
      this.saveStatus.set('error');
      this.saveMessage.set('Formular konnte nicht verarbeitet werden.');
      return;
    }
    const editingBookingId = this.editingBookingId();
    const accountId = this.form().accountId;
    // Generationszähler erfassen (Batch D Review, P2): nach dem Commit
    // wird das Formular nur zurückgesetzt, wenn sich nichts geändert
    // hat — eine zwischenzeitliche Änderung wird nie still gelöscht.
    const savedFormGeneration = this.formGeneration;
    this.saving.set(true);
    this.saveStatus.set('idle');
    this.saveMessage.set(null);
    try {
      if (editingBookingId === null) {
        // Neu erfassen: eine Transaktion (Buchung + Splits + Outbox +
        // Zähler, Batch B) — erst jetzt ist die Eingabe sicher.
        await this.bookingsRepository.save(input, new Date());
        this.saveStatus.set('saved');
        this.saveMessage.set('Gespeichert in der Outbox.');
      } else {
        // Ändern (Batch E, Design D10): `update` ersetzt die wartende
        // Buchung — dieselbe `operationId`, Payload und Hash werden
        // ersetzt (keine zweite Outbox-Zeile, Batch B).
        await this.bookingsRepository.update(editingBookingId, input, new Date());
        this.clearEditMode();
        this.saveStatus.set('saved');
        this.saveMessage.set('Änderungen gespeichert.');
      }
      if (this.formGeneration === savedFormGeneration) {
        // Direktes `form.set` (nicht `setForm`): die eigene Reset-
        // Mutation muss die `saving`-Sperre umgehen dürfen.
        // Der Generationszähler wird mitgezählt (Batch D Review, P2):
        // ein vor dem Save gestartetes loadDraft() erkennt über die
        // geänderte Generation, dass der Save inzwischen abgeschlossen
        // ist, und wendet eine verspätete Entwurf-Antwort nicht an —
        // der gespeicherte Entwurf bleibt erhalten und kann nach dem
        // Save bewusst neu geladen werden.
        this.formGeneration += 1;
        this.form.set({ ...createDefaultForm(todayInBerlin()), accountId });
      }
    } catch (e) {
      this.saveStatus.set('error');
      this.saveMessage.set(
        e instanceof BookingValidationError
          ? e.message
          : `Speichern fehlgeschlagen: ${describeError(e)}`,
      );
    } finally {
      this.saving.set(false);
    }
  }

  // --- Bearbeitung wartender Buchungen (Batch E, Design D10) --------------

  /**
   * Aktiviert den Ändern-Modus (Batch E, P5/D10): lädt die
   * wartende Buchung aus der Outbox und befüllt das Formular per
   * `fromBookingDetail`. Wird aufgerufen, wenn die Route
   * `erfassen/:bookingId` ist (`ngOnInit`); das Beenden des
   * Ändern-Modus übernimmt `save()` (Update) bzw. `cancelEdit()`.
   *
   * In-Flight-Checks: wenn `saving` aktiv ist oder sich das
   * Formular geändert hat (`formGeneration`), wird NICHT geladen.
   *
   * Öffentlich, weil `ngOnInit` den Routen-Parameter daraus löst
   * UND die Specs den Ändern-Modus direkt aktivieren (P5).
   */
  async enterEdit(bookingId: string): Promise<void> {
    if (this.saving()) {
      return;
    }
    if (this.editingBookingId() !== null) {
      this.saveStatus.set('error');
      this.saveMessage.set('Buchung wird bereits bearbeitet.');
      return;
    }
    const generationAtStart = this.formGeneration;
    const booking = await this.bookingsRepository.get(bookingId);
    if (booking === null) {
      // Buchung nicht gefunden oder nicht in der Outbox (z. B.
      // bereits versendet): Hinweis, Formular unverändert.
      this.saveStatus.set('error');
      this.saveMessage.set('Buchung nicht gefunden oder nicht in der Outbox.');
      return;
    }
    // In-Flight-Check: Formular wurde zwischen Start und Lade-Antwort
    // geändert oder ein Save läuft — die Buchung wird nicht geladen.
    if (this.saving() || this.formGeneration !== generationAtStart) {
      return;
    }
    this.editingBookingId.set(bookingId);
    this.editBannerText.set(
      `Buchung vom ${booking.date} · ${formatCents(booking.totalAmountCents)} € wird bearbeitet.`,
    );
    this.setForm(fromBookingDetail(booking));
    this.saveStatus.set('idle');
    this.saveMessage.set(null);
  }

  /**
   * Beendet den Ändern-Modus ohne Speichern (Batch E, Design D10):
   * `editingBookingId.set(null)`, Formular wird zurückgesetzt
   * (Kontoselektion bleibt — konsistent mit dem Reset nach dem
   * Speichern). Die gespeicherte Buchung ist unangetastet.
   */
  cancelEdit(): void {
    if (this.saving() || this.editingBookingId() === null) {
      return;
    }
    const accountId = this.form().accountId;
    this.clearEditMode();
    // Direktes `form.set` + Generation mitzählen: wie der Reset nach
    // dem Speichern — eine zwischenzeitlich gestartete asynchrone
    // Operation erkennt die geänderte Generation.
    this.formGeneration += 1;
    this.form.set({ ...createDefaultForm(todayInBerlin()), accountId });
    this.saveStatus.set('idle');
    this.saveMessage.set(null);
  }

  /**
   * Ändern-Modus für Template und Tests (Batch E, P5): `null` =
   * kein Modus, sonst `{ bookingId }` der bearbeiteten Buchung.
   */
  editMode(): { bookingId: string } | null {
    const bookingId = this.editingBookingId();
    return bookingId === null ? null : { bookingId };
  }

  /** Leert den Ändern-Modus (ID + Banner). */
  private clearEditMode(): void {
    this.editingBookingId.set(null);
    this.editBannerText.set(null);
  }

  // --- Entwurf (Design D8) --------------------------------------------------

  /** Speichert den aktuellen Formularstand als Entwurf (auch
   *  teilweise gefüllt — es muss kein gültiges Formular sein).
   */
  async saveDraft(): Promise<void> {
    // Batch E (Design D10): während einer Bearbeitung gehört das
    // Formular zur Buchung — es darf kein Entwurf gespeichert werden.
    if (this.editingBookingId() !== null) {
      this.saveStatus.set('info');
      this.saveMessage.set('Keine Entwürfe während einer Änderung.');
      return;
    }
    try {
      await this.draftsRepository.save(
        DRAFT_ID,
        DRAFT_NAME,
        serializeDraft(this.form()),
        new Date(),
      );
      this.draftLoaded.set(true);
      this.saveStatus.set('saved');
      this.saveMessage.set('Entwurf gespeichert.');
    } catch (e) {
      this.saveStatus.set('error');
      this.saveMessage.set(`Entwurf konnte nicht gespeichert werden: ${describeError(e)}`);
    }
  }

  /** Lädt den Entwurf (überschreibt das Formular).
   *
   * Fehlfälle: kein Entwurf → Hinweis; beschädigtes JSON →
   *  Entwurf verworfen + Hinweis.
   */
  async loadDraft(): Promise<void> {
    // Start-Sperre (Batch D Review, P2): kein neuer Entwurf-Ladevorgang
    // während einer laufenden Save-Transaktion.
    if (this.saving()) {
      this.saveStatus.set('error');
      this.saveMessage.set('Speichern läuft gerade — Entwurf wird nicht geladen.');
      return;
    }
    // Batch E (Design D10): während einer Bearbeitung gehört das
    // Formular zur Buchung — ein Entwurf darf es nicht überschreiben.
    if (this.editingBookingId() !== null) {
      this.saveStatus.set('info');
      this.saveMessage.set('Keine Entwürfe während einer Änderung.');
      return;
    }
    const generationAtStart = this.formGeneration;
    try {
      const draft = await this.draftsRepository.get(DRAFT_ID);
      if (draft === null) {
        this.saveStatus.set('error');
        this.saveMessage.set('Kein Entwurf vorhanden.');
        return;
      }
      const model = parseDraft(draft.data);
      if (model === null) {
        await this.draftsRepository.delete(DRAFT_ID);
        this.draftLoaded.set(false);
        this.saveStatus.set('error');
        this.saveMessage.set('Entwurf beschädigt — verworfen.');
        return;
      }
      // In-flight-Check (Batch D Review, P2): hat während des Loads das
      // Speichern begonnen, oder wurde das Formular in der Zwischenzeit
      // geändert, wird das Ergebnis nicht angewendet — der Entwurf
      // bleibt gespeichert und kann später neu geladen werden.
      if (this.saving()) {
        this.saveStatus.set('error');
        this.saveMessage.set('Speichern läuft gerade — Entwurf wird nicht geladen.');
        return;
      }
      if (this.formGeneration !== generationAtStart) {
        this.saveStatus.set('error');
        this.saveMessage.set(
          'Formular wurde in der Zwischenzeit geändert — Entwurf nicht geladen.',
        );
        return;
      }
      this.setForm(model);
      this.draftLoaded.set(false);
      this.saveStatus.set('saved');
      this.saveMessage.set('Entwurf geladen.');
    } catch (e) {
      this.saveStatus.set('error');
      this.saveMessage.set(`Entwurf konnte nicht geladen werden: ${describeError(e)}`);
    }
  }

  /** Verwirft den Entwurf. */
  async discardDraft(): Promise<void> {
    // Batch E (Design D10): während einer Bearbeitung gehört das
    // Formular zur Buchung — es darf kein Entwurf verworfen werden.
    if (this.editingBookingId() !== null) {
      this.saveStatus.set('info');
      this.saveMessage.set('Keine Entwürfe während einer Änderung.');
      return;
    }
    try {
      await this.draftsRepository.delete(DRAFT_ID);
      this.draftLoaded.set(false);
      this.saveStatus.set('saved');
      this.saveMessage.set('Entwurf verworfen.');
    } catch (e) {
      this.saveStatus.set('error');
      this.saveMessage.set(`Entwurf konnte nicht verworfen werden: ${describeError(e)}`);
    }
  }

  // --- Anzeige-Helfer -------------------------------------------------------

  /** Formatiert einen Cent-Betrag für die Anzeige ("—" bei null). */
  formatCentsDisplay(cents: number | null): string {
    return cents === null ? '—' : formatCents(cents);
  }

  /** Vorzeichenbehaftete Anzeige der Abweichung (z. B. "−0,50"). */
  formatDeviation(cents: number | null): string {
    if (cents === null) {
      return '—';
    }
    return (cents < 0 ? '−' : '') + formatCents(Math.abs(cents));
  }

  /** Platzhalter für die markierte Steuer-Summe (= Splitbetrag). */
  markedAmountPlaceholder(split: EntrySplitModel): string {
    const c = parseAmount(split.amountInput);
    return c === null || c === undefined || c <= 0 ? '0,00' : formatCents(c);
  }

  // --- intern ----------------------------------------------------------------

  private snapshotOptions<T extends { id: string; name: string; status: string }>(
    items: Map<string, T> | undefined,
  ): { id: string; label: string }[] {
    if (items === undefined || items === null) {
      return [];
    }
    return [...items.values()]
      .filter((i) => i.status === 'active')
      .sort((a, b) => a.name.localeCompare(b.name))
      .map((i) => ({ id: i.id, label: i.name }));
  }
}

function describeError(err: unknown): string {
  if (err instanceof Error) {
    return err.message;
  }
  return String(err);
}

/** ID des Eingabe-Entwurfs (Design D8: fester Eintrag in `drafts`). */
const DRAFT_ID = 'expense-entry';

/** Anzeigename des Eingabe-Entwurfs (Spalte `name`). */
const DRAFT_NAME = 'Ausgabeeingabe';
