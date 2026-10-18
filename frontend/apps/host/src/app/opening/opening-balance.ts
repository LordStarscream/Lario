import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
  type Signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiError as ApiErrorClass } from '../../generated/core/ApiError';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import { CatalogService } from '../../api/catalogs';
import { PendingOperation } from '../../api/pending-operation';
import {
  centsToEuroText,
  formatCents,
  formatDate,
  parseEuroToCents,
  todayIso,
} from '../../api/money';
import { BalancesPanel } from '../shared/balances-panel/balances-panel';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import type { BalancesDto } from '../../generated/models/BalancesDto';
import type { OpeningBalanceListItemDto } from '../../generated/models/OpeningBalanceListItemDto';
import type { PoolAllocationCommand } from '../../generated/models/PoolAllocationCommand';

interface DistributionRow {
  key: number;
  poolId: string;
  amountText: string;
}

/**
 * Anfangsbestände erfassen (Kernfunktion 3, Zwischenstep 7.5):
 * - Route `anfangsbestaende`: Liste vorhandener Anfangsbestände (A3)
 *   plus Erfassungsformular für einen neuen Eintrag.
 * - Route `anfangsbestaende/:id/korrektur`: vorgelegtes Korrektur-
 *   formular (dieselbe Komponente, Modus aus der Route — dasselbe
 *   Muster wie die Ausgaben-Korrektur). Anfangsbestände sind
 *   designgemäß nicht stornierbar (A4); ein fehlerhafter Eintrag wird
 *   über den definierten Korrekturweg in-place korrigiert (A5).
 */
@Component({
  selector: 'app-opening-balance',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BalancesPanel],
  templateUrl: './opening-balance.html',
  styleUrl: './opening-balance.css',
})
export class OpeningBalance {
  private readonly router = inject(Router);
  private readonly catalog = inject(CatalogService);
  private readonly route = inject(ActivatedRoute);
  private readonly errors = inject(ApiErrorHandler);
  /**
   * Stabile Operation-Id (Review R3 P1 / R5-1): nach verlorener Antwort
   * sendet der nächste Klick mit unveränderter Daten dieselbe Operation
   * (gleiche Id + Payload) — idempotente Quittung statt doppeltem
   * Anfangsbestand. Der Zustand liegt im Root-Service: der 401-Login-
   * Redirect zerstört das Formular, behält aber die gehaltene Operation.
   * Der Slot ist pro Modus fest ('open' Erstellung / 'open-corr'
   * Korrektur), analog den 'exp'/'corr'-Slots der Ausgaben-Formulare.
   */
  private readonly pendingOp = inject(PendingOperation);
  readonly correctionId: string | null;
  readonly isCorrection: boolean;
  private readonly opPrefix: string;
  readonly outcomeUncertain: Signal<boolean>;
  /** Der zu korrigierende Eintrag (im Korrekturmodus geladen). */
  readonly target = signal<OpeningBalanceListItemDto | null>(null);
  /**
   * Fingerprint der Vorbelegung (Review R3 P2, Analogie zur
   * Ausgaben-Korrektur): bleibt das Formular unverändert, werden die
   * Felder als null gesendet — der Server hält die Originalwerte,
   * statt sie still identisch neu zu setzen.
   */
  private originalSnapshot: string | null = null;
  private keyBase = 1;

  readonly accounts = this.catalog.accounts;
  readonly pools = this.catalog.pools;

  date = todayIso();
  accountId = '';
  totalText = '';
  note = '';
  distribution: DistributionRow[] = [];

  readonly busy = signal(false);
  readonly error = signal<ApiErrorInfo | null>(null);
  /** Ladezustand: Korrektur-Vorbelegung bzw. Auflistung (A3). */
  readonly loading = signal(false);
  readonly loadError = signal<ApiErrorInfo | null>(null);
  /** Ladefehler der Liste (A3) — blockiert das Formular nicht. */
  readonly listError = signal<ApiErrorInfo | null>(null);

  /**
   * Vorhandene Anfangsbestände (Zwischenstep 7.5, A3): Liste über dem
   * Erfassungsformular. Ein Ladefehler blockiert das Formular nicht.
   */
  readonly entries = signal<OpeningBalanceListItemDto[]>([]);

  /**
   * Aktuelle Salden (Review R7-8): dasselbe gemeinsame Salden-Panel wie
   * in der Buchungsliste — bei der Verteilung eines Anfangsbestands
   * sind die Kontostände und Poolbestände direkt ablesbar. Daten
   * ausschließlich aus dem abgeleiteten GET /api/v1/balances.
   * Ergänzend: ein Ladefehler blockiert das Formular nicht (ohne Panel).
   */
  readonly balances = signal<BalancesDto | null>(null);

  /** Salden laden (R7-8); Fehler bleiben lokal und unsichtbar. */
  private async loadBalances(): Promise<void> {
    try {
      this.balances.set(await JournalEndpointsService.getBalances());
    } catch {
      this.balances.set(null);
    }
  }

  /**
   * Erfolgsmeldung nach erfolgreichem Speichern (7.5, offener Punkt):
   * Der Nutzer bleibt auf derselben Seite und erfasst das nächste
   * Konto — die Meldung bleibt sichtbar, bis die nächste Einreichung
   * startet (oder die Seite verlassen wird).
   */
  readonly savedMessage = signal<string | null>(null);

  /**
   * 7.5 Restfehler: Konten, die soeben erfolgreich gespeichert wurden und
   * noch nicht in der (neu geladenen) Eintragsliste stehen. Scheitert das
   * Nachladen nach dem Speichern, wäre die Eintragsliste leer/veraltet
   * und das eingerichtete Konto würde wieder auswählbar — diese Auf-
   * zeichnung hält es heraus, bis ein Laden der Liste erfolgreich ist.
   */
  readonly newlySavedAccountIds = signal<string[]>([]);

  /**
   * Review R7-3 (Einrichtungsvorgang): Konten, die noch keinen
   * Anfangsbestand tragen — im Neuanlage-Formular sind bereits
   * eingerichtete Konte aus der Auswahl herausgefiltert (einschließlich
   * soeben gespeicherter Konten, deren Eintrag die Liste nach einem
   * fehlgeschlagenen Nachladen noch nicht zeigt).
   */
  readonly availableAccounts = computed(() => {
    const taken = new Set([
      ...this.entries().map((entry) => entry.accountId),
      ...this.newlySavedAccountIds(),
    ]);
    return this.catalog.accounts().filter((account) => !taken.has(account.id));
  });

  /**
   * Review R7-3 (Einrichtungsvorgang): alle aktiven Konten haben bereits
   * einen Anfangsbestand — Neuanlagen sind nicht mehr möglich, statt des
   * Formulars wird eine Zustandsanzeige gezeigt (nur wenn Katalog und
   * Liste erfolgreich geladen sind). „Mindestens ein Eintrag existiert“
   * gilt zusätzlich für soeben gespeicherte Konten, deren Eintrag ein
   * fehlgeschlagenes Nachladen der Liste noch nicht enthält (7.5
   * Restfehler).
   */
  readonly allConfigured = computed(
    () =>
      this.catalog.loaded()
      && this.catalog.accounts().length > 0
      && (this.entries().length > 0
        || this.newlySavedAccountIds().length > 0)
      && this.availableAccounts().length === 0,
  );

  /**
   * Review R7-3 (Einrichtungsvorgang): Optionsliste des Konto-Felds —
   * Neuanlage filtert bereits eingerichtete Konten heraus; die Korrektur
   * behält den vollen Katalog (Konto-Wechsel ist ein Korrekturfeld; ein
   * belegtes Zielkonto lehnt der Server mit Hinweis ab).
   */
  readonly accountOptions = computed(() =>
    this.isCorrection ? this.catalog.accounts() : this.availableAccounts(),
  );

  constructor() {
    const id = this.route.snapshot.paramMap.get('id');
    this.correctionId = id;
    this.isCorrection = id !== null;
    this.opPrefix = this.isCorrection ? 'open-corr' : 'open';
    this.outcomeUncertain = this.pendingOp.outcomeUncertain(this.opPrefix);
    if (id !== null) {
      // Korrekturmodus: Vorbelegung lädt den Katalog mit (eigener Aufruf).
      void this.loadCorrection(id);
    } else {
      void this.catalog.load();
      void this.loadEntries();
    }
    // Salden-Panel (R7-8): in beiden Modus verfügbar.
    void this.loadBalances();
  }

  // ---------- Liste (A3) ----------

  /** Anfangsbestände laden (A3); Fehler bleiben lokal und sichtbar. */
  private async loadEntries(): Promise<void> {
    this.loading.set(true);
    this.listError.set(null);
    try {
      this.entries.set(await JournalEndpointsService.listOpeningBalances());
      // Gelungenes Laden zeigt den tatsächlichen Stand — die lokale
      // Aufzeichnung der soeben gespeicherten Konten ist damit
      // überflüssig (7.5 Restfehler).
      this.newlySavedAccountIds.set([]);
    } catch (error) {
      this.entries.set([]);
      this.listError.set(this.errors.handle(error));
    } finally {
      this.loading.set(false);
    }
  }

  /** Korrektur-Route öffnen (A5) — die URL trägt den Modus. */
  goCorrection(id: string): void {
    // Absoluter Pfad (gleiche Konvention wie die Ausgaben-Korrektur):
    // ein relativer navigate() auflöst von der Wurzel und würde die
    // falsche Route treffen.
    void this.router.navigate(['/anfangsbestaende', id, 'korrektur']);
  }

  /** Zurück zur Liste (Korrekturmodus). */
  goBack(): void {
    void this.router.navigate(['/anfangsbestaende']);
  }

  // ---------- Korrektur vorbelegen (A5) ----------

  private async loadCorrection(id: string): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      // Erst Katalog, dann Eintrag: Konto- und Poolnamen der
      // Vorbelegung (und des Banners) kommen aus dem Katalog.
      await this.catalog.load();
      const list = await JournalEndpointsService.listOpeningBalances();
      const entry = list.find((candidate) => candidate.id === id);
      if (entry === undefined) {
        this.loadError.set({
          code: 'not_found',
          message:
            'Der gewählte Anfangsbestand wurde nicht gefunden — die Id ' +
            'ist falsch oder der Eintrag existiert nicht mehr.',
          expectedBaseRevision: null,
          actualBaseRevision: null,
        });
        return;
      }
      this.target.set(entry);
      this.accountId = entry.accountId;
      this.date = entry.date;
      this.totalText = centsToEuroText(entry.total);
      this.note = entry.note ?? '';
      this.distribution = entry.distribution.map((allocation) => ({
        key: this.keyBase++,
        poolId: allocation.poolId,
        amountText: centsToEuroText(allocation.amount),
      }));
      this.originalSnapshot = this.snapshot();
    } catch (error) {
      this.loadError.set(this.errors.handle(error));
    } finally {
      this.loading.set(false);
    }
  }

  /** Manuelles Zurücksetzen der gehaltene Operation (Review R5-1). */
  resetPendingOperation(): void {
    this.pendingOp.release(this.opPrefix);
  }

  /**
   * Aktueller Formularzustand als stabiler Fingerprint — dient sowohl
   * der Pending-Operation (Idempotenz) als auch der "unverändert"-
   * Erkennung für die Korrektur.
   */
  private snapshot(): string {
    return JSON.stringify({
      date: this.date,
      total: parseEuroToCents(this.totalText),
      accountId: this.accountId,
      distribution: this.distribution.map((row) => ({
        poolId: row.poolId,
        amount: parseEuroToCents(row.amountText) ?? 0,
      })),
      // Review R7-5: roher Wert — „gelöscht“ (‚'‚) und „unverändert“
      // (Originaltext) müssen sich im Fingerprint unterscheiden.
      note: this.note.trim(),
    });
  }

  /** Das Formular entspricht der Vorbelegung — nichts zu korrigieren. */
  correctionUnchanged(): boolean {
    return (
      this.originalSnapshot !== null && this.snapshot() === this.originalSnapshot
    );
  }

  // ---------- Verteilung ----------

  addRow(): void {
    this.distribution = [
      ...this.distribution,
      { key: this.keyBase++, poolId: '', amountText: '' },
    ];
  }

  removeRow(key: number): void {
    this.distribution = this.distribution.filter((row) => row.key !== key);
  }

  distributionSumCents(): number {
    return this.distribution.reduce(
      (sum, row) => sum + (parseEuroToCents(row.amountText) ?? 0),
      0,
    );
  }

  unallocatedCents(): number {
    const total = parseEuroToCents(this.totalText) ?? 0;
    return total - this.distributionSumCents();
  }

  issue(): string | null {
    if (this.isCorrection && this.correctionUnchanged()) {
      return 'Keine Änderungen gegenüber dem aktuellen Eintrag — nichts zu korrigieren.';
    }
    if (!this.date) {
      return 'Datum fehlt.';
    }
    if (!this.accountId) {
      return 'Konto fehlt.';
    }
    const total = parseEuroToCents(this.totalText);
    if (total === null || total <= 0) {
      return 'Gesamtbetrag fehlt oder ungültig.';
    }
    const usedPoolIds = new Set<string>();
    for (const row of this.distribution) {
      if (!row.poolId) {
        return 'Jede Verteilung benötigt einen Pool.';
      }
      if (usedPoolIds.has(row.poolId)) {
        return 'Ein Pool kann nur einmal verteilt werden.';
      }
      usedPoolIds.add(row.poolId);
      const amount = parseEuroToCents(row.amountText);
      if (amount === null || amount <= 0) {
        return 'Jeder Verteilungsbetrag muss positiv und gültig sein.';
      }
    }
    if (this.distributionSumCents() > total) {
      return 'Die Verteilung darf den Gesamtbetrag nicht überschreiten.';
    }
    return null;
  }

  get issueText(): string | null {
    return this.issue();
  }

  // ---------- Einreichen ----------

  async submit(): Promise<void> {
    if (this.busy() || this.issue() !== null) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.savedMessage.set(null);
    try {
      const distribution: PoolAllocationCommand[] = this.distribution.map(
        (row) => ({
          poolId: row.poolId,
          amount: parseEuroToCents(row.amountText) as number,
        }),
      );
      // Review R7-5 (Note-Semantik): der rohe getrimmte Wert — '' ist
      // eine explizite Löschung; „unverändert“ wird unten als null
      // gesendet (noteUnchanged).
      const note = this.note.trim();
      const total = parseEuroToCents(this.totalText) as number;
      // Review R4 F1: unklarer Ausgang + geänderte Daten → Blocken.
      // Eine neue Id würde eine zweite Buchung erzeugen können, wenn die
      // alte Operation bereits committet ist.
      const id = this.pendingOp.nextId(this.opPrefix, this.snapshot());
      if (id === null) {
        this.error.set({
          code: 'uncertain_operation_blocked',
          message:
            'Einreichung blockiert: Der Ausgang des letzten Versuchs ist unklar und die Daten wurden seitdem geändert. Prüfe die Buchungsliste, ob der vorherige Eintrag gebucht wurde (ggf. korrigieren), und setze die Operation unten zurück, bevor du neue Daten einreichst.',
          expectedBaseRevision: null,
          actualBaseRevision: null,
        });
        this.busy.set(false);
        return;
      }
      if (this.isCorrection) {
        const target = this.target();
        // Unveränderte Felder bleiben null — der Server hält die
        // Originalwerte (Review R3 P2).
        // Review R7-5: unveränderte Notiz sendet null (Server hält das
        // Original); eine geleerte Notiz sendet '' (explizite Löschung).
        const noteUnchanged = note === (target?.note ?? '');
        const distributionUnchanged =
          JSON.stringify(distribution) ===
          JSON.stringify(
            (target?.distribution ?? []).map((allocation) => ({
              poolId: allocation.poolId,
              amount: allocation.amount,
            })),
          );
        await JournalWriteEndpointsService.correctBooking(
          this.correctionId as string,
          {
            operationId: id,
            expectedBaseRevision: Number(target?.baseRevision ?? 0),
            date: this.date === target?.date ? null : this.date,
            note: noteUnchanged ? null : note,
            accountId:
              this.accountId === target?.accountId ? null : this.accountId,
            totalAmount:
              total === Number(target?.total) ? null : total,
            splits: null,
            allocations: distributionUnchanged ? null : distribution,
          },
        );
        this.pendingOp.release(this.opPrefix);
        await this.router.navigate(['/anfangsbestaende']);
      } else {
        await JournalWriteEndpointsService.createOpeningBalance({
          id,
          date: this.date,
          total,
          accountId: this.accountId,
          distribution,
          // Neuanlage: leere Notiz bleibt null (es gibt kein Original).
          note: note === '' ? null : note,
        });
        const savedAccountName =
          this.catalog
            .accounts()
            .find((acc) => acc.id === this.accountId)?.name ?? this.accountId;
        this.pendingOp.release(this.opPrefix);
        // 7.5 Restfehler: das soeben eingerichtete Konto muss auch dann
        // nicht wieder auswählbar sein, wenn das Nachladen unten
        // fehlschlägt (Eintragsliste wird leer/veraltet). Die Auf-
        // zeichnung wird erst von einem erfolgreichen Laden gelöscht.
        this.newlySavedAccountIds.set([
          ...this.newlySavedAccountIds(),
          this.accountId,
        ]);
        // 7.5, offener Punkt: nach dem Speichern auf derselben Seite
        // bleiben — das Formular wird für das nächste Konto
        // zurückgesetzt. Der Stichtag bleibt gesetzt (Einrichtungsvor-
        // gang: alle Konten teilen üblicherweise denselben Stichtag);
        // er ist weiterhin pro Konto änderbar. Liste (das eingerichtete
        // Konto fällt automatisch aus der Auswahl heraus) und Salden-
        // Panel werden aktualisiert.
        this.savedMessage.set(
          `Anfangsbestand für ${savedAccountName} gebucht — nächstes ` +
            'Konto erfassen.',
        );
        this.accountId = '';
        this.totalText = '';
        this.note = '';
        this.distribution = [];
        void this.loadEntries();
        void this.loadBalances();
      }
      // 7.5, offener Punkt: die Komponente bleibt nach dem Erfolg auf
      // derselben Seite aktiv (vorher zerstörte die Navigation sie) —
      // das Formular muss für den nächsten Eintrag freigeschaltet sein.
      this.busy.set(false);
    } catch (err) {
      this.error.set(this.errors.handle(err));
      // Definitive Ablehnung (4xx): neue Id beim nächsten Versuch —
      // aber nur ohne vorher unklaren Versuch: ein unklarer
      // Vorversuch kann bereits committet sein (Review R5-1); dann
      // bleibt die Operation gehalten und wird idempotent wiedergesendet.
      const definitive =
        err instanceof ApiErrorClass && err.status >= 400 && err.status < 500;
      if (definitive) {
        if (!this.pendingOp.outcomeUncertain(this.opPrefix)()) {
          this.pendingOp.release(this.opPrefix);
        }
      } else {
        this.pendingOp.markOutcomeUncertain(this.opPrefix);
      }
      this.busy.set(false);
    }
  }

  // ---------- Anzeige für das Template ----------

  formatCents = formatCents;
  formatDate = formatDate;

  /** Unverteilter Rest (Client-Seite: DTO trägt keine Summenfelder). */
  unallocatedOf(entry: OpeningBalanceListItemDto): number {
    const total = Number(entry.total);
    const distributed = entry.distribution.reduce(
      (sum, allocation) => sum + Number(allocation.amount),
      0,
    );
    return total - distributed;
  }

  accountName(id: string): string {
    const account = this.catalog.accounts().find((entry) => entry.id === id);
    return account ? account.name : id;
  }

  poolName(id: string): string {
    const pool = this.catalog.pools().find((entry) => entry.id === id);
    return pool ? pool.name : id;
  }
}
