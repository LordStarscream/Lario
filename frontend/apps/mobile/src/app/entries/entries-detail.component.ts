// Batch E (P5) — Eingaben-Detail: eine lokale Ausgabe im Vollbild —
// Felder, Splits (inkl. Steuer-Block) und Aktionen:
// - „Ändern“ (nur queued): Link `/erfassen/:bookingId` (die
//   Erfassung aktiviert dort den Ändern-Modus aus dem Routen-
//   Parameter);
// - „Verwerfen“ (nur queued): Zwei-Stufen-Bestätigung, danach
//   `bookingsRepository.discard` und Navigation zurück zur Liste.
//
// Zoneless: asynchrone Zustände sind Signals; OnPush + Signale.
import {
  ChangeDetectionStrategy,
  Component,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  BookingsRepository,
  BookingStateError,
  type LocalBookingDetail,
} from '../db/bookings.repository';
import { SnapshotRepository } from '../db/snapshot.repository';
import { formatCents } from '../amount';

@Component({
  selector: 'app-entries-detail',
  templateUrl: './entries-detail.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
})
export class EntriesDetailComponent implements OnInit {
  // inject() statt Constructor-Parameter (ESLint prefer-inject).
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly bookingsRepository = inject(BookingsRepository);
  private readonly snapshotRepository = inject(SnapshotRepository);

  /** true, bis Buchung + Snapshot geladen sind. */
  readonly loading = signal(true);
  /** `null` = Buchung nicht gefunden (sonst: Detail). */
  readonly detail = signal<LocalBookingDetail | null>(null);
  /** Lade-Fehler (Buchungs- oder Snapshot-Load). */
  readonly loadError = signal<string | null>(null);
  /** Aktionsergebnis (z. B. Verwerfen fehlgeschlagen). */
  readonly actionMessage = signal<string | null>(null);
  /**
   * Zwei-Stufen-Bestätigung des Verwerfens (Design D10): `false` =
   * keine offene Bestätigung; erst der zweite Klick verwirft.
   */
  readonly confirmingDiscard = signal(false);
  /** true, solange ein Verwerfen läuft (Sperre gegen Doppeltippen). */
  readonly discarding = signal(false);

  /** Kontonamen aus dem Snapshot (leeres Map ohne Snapshot). */
  readonly accountNames = signal<Map<string, string>>(new Map());
  readonly poolNames = signal<Map<string, string>>(new Map());
  readonly categoryNames = signal<Map<string, string>>(new Map());

  ngOnInit(): void {
    void this.load();
  }

  /** Lädt Buchung und Snapshot (Beides parallel; Snapshot-Fehler
   * sind unkritisch — rohe IDs werden angezeigt).
   */
  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    this.confirmingDiscard.set(false);
    const bookingId = this.route.snapshot.paramMap.get('bookingId');
    if (bookingId === null) {
      this.detail.set(null);
      this.loading.set(false);
      return;
    }
    const [detailResult, snapshotResult] = await Promise.allSettled([
      this.bookingsRepository.get(bookingId),
      this.snapshotRepository.loadSnapshot(),
    ]);
    if (detailResult.status === 'rejected') {
      // `get()` wirft `null` nicht — ein Reject ist immer ein Fehler;
      // die „nicht gefunden“-Leere kommt über `detail === null`.
      this.loadError.set(
        `Buchung konnte nicht geladen werden: ${describeError(detailResult.reason)}`,
      );
      this.detail.set(null);
    } else {
      this.detail.set(detailResult.value);
    }
    if (snapshotResult.status === 'fulfilled' && snapshotResult.value !== null) {
      const accounts = new Map<string, string>();
      const pools = new Map<string, string>();
      const categories = new Map<string, string>();
      for (const [id, account] of snapshotResult.value.accounts) {
        accounts.set(id, account.name);
      }
      for (const [id, pool] of snapshotResult.value.pools) {
        pools.set(id, pool.name);
      }
      for (const [id, category] of snapshotResult.value.categories) {
        categories.set(id, category.name);
      }
      this.accountNames.set(accounts);
      this.poolNames.set(pools);
      this.categoryNames.set(categories);
    }
    this.loading.set(false);
  }

  /** Anzeigename (Snapshot-Name, sonst die rohe ID). */
  accountLabel(accountId: string): string {
    return this.accountNames().get(accountId) ?? accountId;
  }

  poolLabel(poolId: string): string {
    return this.poolNames().get(poolId) ?? poolId;
  }

  categoryLabel(categoryId: string): string {
    return this.categoryNames().get(categoryId) ?? categoryId;
  }

  formatAmount(cents: number): string {
    return formatCents(cents);
  }

  /**
   * Status-Anzeige (P5): `queued` → „ausstehend“, sonst roher Status
   * (Schritt 8 kennt nur `queued`; `confirmed`/`unknown` werden roh
   * angezeigt).
   */
  statusLabel(status: LocalBookingDetail['status']): string {
    return status === 'queued' ? 'ausstehend' : status;
  }

  /**
   * Verwerfen mit Zwei-Stufen-Bestätigung (Design D10): erster Klick
   * zeigt die Bestätigung, zweiter Klick verwirft und navigiert
   * zurück zur Liste. Fehler (z. B. `BookingStateError`) → Meldung,
   * keine Navigation.
   */
  async onDiscard(): Promise<void> {
    const detail = this.detail();
    if (detail === null || this.discarding()) {
      return;
    }
    if (!this.confirmingDiscard()) {
      this.confirmingDiscard.set(true);
      this.actionMessage.set(null);
      return;
    }
    this.confirmingDiscard.set(false);
    this.discarding.set(true);
    try {
      await this.bookingsRepository.discard(detail.bookingId);
      // Hinweis in der Liste: Router-State (Liste liest
      // `discardMessage` beim nächsten Laden, P5).
      await this.router.navigate(['/eingaben'], {
        state: { discardMessage: 'Buchung verworfen.' },
      });
    } catch (e) {
      this.actionMessage.set(
        e instanceof BookingStateError
          ? `Verwerfen nicht möglich: ${e.message}`
          : `Verwerfen fehlgeschlagen: ${describeError(e)}`,
      );
    } finally {
      this.discarding.set(false);
    }
  }

  /** Bestätigungsblock zurücksetzen (Button „Abbrechen“). */
  cancelDiscard(): void {
    this.confirmingDiscard.set(false);
  }
}

function describeError(err: unknown): string {
  if (err instanceof Error) {
    return err.message;
  }
  return String(err);
}
