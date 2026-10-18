import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import { formatCents, formatDate } from '../../../api/money';
import type { BalancesDto } from '../../../generated/models/BalancesDto';

/**
 * Gemeinsames Salden-Panel (Zwischenstep 7.5, Review R7-8/R7-9):
 * aktuelle Konten- und Pool-Salden plus unzugeordnete Anfangsbestände.
 *
 * Rein präsentiell: die Daten kommen ausschließlich aus dem abgeleiteten
 * `GET /api/v1/balances` (Rechenvertrag — keine clientseitige Saldo-
 * Rechnung). Das Panel berechnet keine Salden, es rendert die
 * serverseitig abgeleiteten Werte und die vom Server gelieferten
 * Summen; die Summenzeile der unzugeordneten Anfangsbestände ist die
 * einfache Summe derselben serverseitig gelieferten Restbeträge.
 *
 * Verwendung: Buchungsliste und Anfangsbestände-Seite — dasselbe Panel,
 * dieselbe Datenquelle (R7-8: die Verteilung eines Anfangsbestands kann
 * an den aktuellen Salden abgelesen werden).
 *
 * R7-9: Der unzugeordnete Rest (Differenz Kontostände − Poolsummen)
 * ist als eigene Sektion sichtbar — pro Anfangsbestand mit Stichtag,
 * Konto und Betrag, plus Summe. Die Differenz im Kopf des Panels ist
 * der serverseitige `accountPoolGap`; stimmt er mit der Summe der
 * unzugeordneten Anfangsbestände nicht überein, bleibt die Abweichung
 * sichtbar (keine stille Angleichung).
 */
@Component({
  selector: 'app-balances-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [],
  templateUrl: './balances-panel.html',
  styleUrl: './balances-panel.css',
})
export class BalancesPanel {
  /** Salden aus `GET /api/v1/balances` (abgeleitet, kein Client-Berechnen). */
  readonly balances = input.required<BalancesDto>();
  readonly open = signal(true);

  readonly formatCents = formatCents;
  readonly formatDate = formatDate;

  /** Summe der unzugeordneten Anfangsbestände in Cent. */
  unallocatedTotal(): number {
    return this.balances().unallocatedOpeningBalances.reduce(
      (sum, entry) => sum + Number(entry.unallocated),
      0,
    );
  }

  toggle(): void {
    this.open.update((value) => !value);
  }
}
