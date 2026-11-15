// Batch E (P5) — Eingaben-Liste: lokal gespeicherte Ausgaben aus
// `listPending()`, sortiert nach createdAtUtc aufsteigend, dann
// bookingId aufsteigend. Jede Zeile: Datum, Total, Status-Text
// (queued → „ausstehend“, sonst roher Status) und die Aktionen
// „Ändern“ (→ `erfassen/:bookingId`) und „Verwerfen“ (→ Detail
// `eingaben/:bookingId`, dort Zweiklick-Bestätigung).
//
// Zoneless: asynchrone Zustände (Liste, Fehler) sind Signals;
// OnPush + Signale (Muster `entry.component.ts`).
import {
  ChangeDetectionStrategy,
  Component,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { Location } from '@angular/common';
import { RouterLink } from '@angular/router';
import {
  BookingsRepository,
  type LocalBookingListItem,
} from '../db/bookings.repository';
import { SnapshotRepository } from '../db/snapshot.repository';
import { formatCents } from '../amount';

@Component({
  selector: 'app-entries',
  templateUrl: './entries.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
})
export class EntriesComponent implements OnInit {
  // inject() statt Constructor-Parameter (ESLint prefer-inject).
  private readonly bookingsRepository = inject(BookingsRepository);
  private readonly snapshotRepository = inject(SnapshotRepository);
  private readonly location = inject(Location);

  /** Liste (null = noch nicht geladen). */
  readonly items = signal<LocalBookingListItem[] | null>(null);
  /** Lade-Fehler („Liste konnte nicht geladen werden: …“). */
  readonly loadError = signal<string | null>(null);
  /**
   * Verwerfen-Hinweis der Detail-View (Router-State
   * `discardMessage`): „Buchung verworfen.“ — wird beim nächsten
   * Laden angezeigt und danach entfernt.
   */
  readonly notice = signal<string | null>(null);
  /** Kontonamen aus dem Snapshot (leeres Map ohne Snapshot). */
  readonly accountNames = signal<Map<string, string>>(new Map());
  /** Poolnamen aus dem Snapshot (leeres Map ohne Snapshot). */
  readonly poolNames = signal<Map<string, string>>(new Map());

  /** Anzeigename des Kontos (Name aus Snapshot, sonst die rohe ID). */
  accountLabel(accountId: string): string {
    return this.accountNames().get(accountId) ?? accountId;
  }

  /** Poolnamen durch „, “ verbunden; unbekannte IDs roh. */
  poolLabel(poolIds: string[]): string {
    return poolIds.map((id) => this.poolNames().get(id) ?? id).join(', ');
  }

  /** Betrag-Formatierung (Template-Zugriff). */
  formatAmount(cents: number): string {
    return formatCents(cents);
  }

  /**
   * Status-Text (P5): `queued` → „ausstehend“, sonst roher Status
   * (Schritt 8 kennt nur `queued`; `confirmed`/`unknown` werden roh
   * angezeigt).
   */
  statusLabel(status: LocalBookingListItem['status']): string {
    return status === 'queued' ? 'ausstehend' : status;
  }

  ngOnInit(): void {
    // Verwerfen-Hinweis aus dem Router-State der Detail-View
    // (Navigation `router.navigate(['/eingaben'], { state: … })`).
    const state = this.location.getState() as {
      discardMessage?: string;
    } | null;
    if (state?.discardMessage) {
      this.notice.set(state.discardMessage);
    }
    void this.refresh();
  }

  /**
   * Lädt Liste und Namen neu. Die Liste wird nach `createdAtUtc`
   * aufsteigend, dann `bookingId` aufsteigend sortiert (P5).
   * Snapshot-Fehler sind unkritisch (die Liste funktioniert ohne
   * Namen — rohe IDs werden angezeigt).
   */
  async refresh(): Promise<void> {
    this.loadError.set(null);
    const [listResult, snapshotResult] = await Promise.allSettled([
      this.bookingsRepository.listPending(),
      this.snapshotRepository.loadSnapshot(),
    ]);
    if (listResult.status === 'fulfilled') {
      const items = [...listResult.value].sort(
        (a, b) =>
          a.createdAtUtc.localeCompare(b.createdAtUtc) ||
          a.bookingId.localeCompare(b.bookingId),
      );
      this.items.set(items);
    } else {
      this.loadError.set(
        `Liste konnte nicht geladen werden: ${describeError(listResult.reason)}`,
      );
    }
    if (snapshotResult.status === 'fulfilled' && snapshotResult.value !== null) {
      const accounts = new Map<string, string>();
      const pools = new Map<string, string>();
      for (const [id, account] of snapshotResult.value.accounts) {
        accounts.set(id, account.name);
      }
      for (const [id, pool] of snapshotResult.value.pools) {
        pools.set(id, pool.name);
      }
      this.accountNames.set(accounts);
      this.poolNames.set(pools);
    }
  }

  /** Verwerfen-Hinweis entfernen (Button). */
  dismissNotice(): void {
    this.notice.set(null);
  }
}

function describeError(err: unknown): string {
  if (err instanceof Error) {
    return err.message;
  }
  return String(err);
}
