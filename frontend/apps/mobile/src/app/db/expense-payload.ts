/**
 * Lokale Eingabe-Typen und Outbox-Payload-Bau (Schritt 8, Design B3).
 *
 * Der Outbox-Payload ist **exakt** die `ExpenseCommand`-Shape des Hosts
 * (`src/Lario.Host/Api/Journal/JournalWriteDtos.cs`), als kompaktes JSON
 * (kein Whitespace) mit **fester Schlüsselreihenfolge**:
 *
 * - Booking: `id, date, totalAmount, accountId, note, splits,
 *   expectedBaseRevision`
 * - Split: `id, amount, poolId, categoryId, note, tax`
 * - tax: `relevant, taxCategoryId, markedAmount, note`
 *
 * `JSON.stringify` erhält bei String-Keys die Einfügereihenfolge — die
 * Schlüssel werden daher in der Reihenfolge oben aufgebaut. Der
 * deterministische String ist die Grundlage des Payload-Nachweises
 * (B1: `payloadHash`).
 *
 * Regeln:
 * - `expectedBaseRevision` ist bei Neuanlage immer `null` (Key vorhanden,
 *   Wert null) — die mobile Anlage kennt noch keine Host-Revision.
 * - Beträge sind Ganzzahl-Centwerte als JSON-Nummern (keine Strings).
 * - `markedAmount`: `tax.markedAmountCents ?? amountCents` — null wird
 *   normalisiert auf den Splitbetrag (deterministisch für den Hash).
 * - `tax: null` bleibt `null` im Payload (kein leeres Objekt).
 * - `date`/`totalAmount`/`accountId`/`note` kommen direkt aus `input`.
 */

/** Steuerkennzeichnung eines Splits (lokale Eingabe). */
export interface LocalTaxInput {
  /** Steuerlich relevant (Schalter). */
  relevant: boolean;
  /** Steuerkategorie; null = keine (bei relevant erlaubt, bei !relevant Pflicht-null). */
  taxCategoryId: string | null;
  /** Nur lokales DB-Feld (kein Payload-Teil): Name für die Anzeige. */
  taxCategoryName: string | null;
  /** null = gesamter Splitbetrag markiert (Standard). */
  markedAmountCents: number | null;
  note: string | null;
}

/** Ein Split einer lokalen Ausgabe (lokale Eingabe). */
export interface LocalSplitInput {
  id: string;
  amountCents: number;
  poolId: string;
  categoryId: string;
  note: string | null;
  /** null = keine explizite Kennzeichnung (Host materialisiert Voreinstellung). */
  tax: LocalTaxInput | null;
}

/** Lokale Ausgabe-Eingabe (eine Buchung). */
export interface LocalExpenseInput {
  /** Fachdatum `yyyy-MM-dd` (Europe/Berlin-Logik der App). */
  date: string;
  totalAmountCents: number;
  accountId: string;
  note: string | null;
  splits: LocalSplitInput[];
}

/**
 * Kompaktes JSON mit fester Schlüsselreihenfolge (siehe Modul-Doku).
 * `bookingId` wird als `id` übernommen; `expectedBaseRevision` ist
 * bei Neuanlage immer `null`.
 */
export function buildExpensePayload(
  bookingId: string,
  input: LocalExpenseInput,
): string {
  const splits = input.splits.map((split) => {
    const tax =
      split.tax === null
        ? null
        : {
            relevant: split.tax.relevant,
            taxCategoryId: split.tax.taxCategoryId,
            // null = gesamter Splitbetrag markiert (B3-Regel).
            markedAmount: split.tax.markedAmountCents ?? split.amountCents,
            note: split.tax.note,
          };
    // Feste Schlüsselreihenfolge (B3): id, amount, poolId, categoryId,
    // note, tax.
    return {
      id: split.id,
      amount: split.amountCents,
      poolId: split.poolId,
      categoryId: split.categoryId,
      note: split.note,
      tax,
    };
  });
  // Feste Schlüsselreihenfolge (B3): id, date, totalAmount, accountId,
  // note, splits, expectedBaseRevision.
  const command = {
    id: bookingId,
    date: input.date,
    totalAmount: input.totalAmountCents,
    accountId: input.accountId,
    note: input.note,
    splits,
    expectedBaseRevision: null,
  };
  return JSON.stringify(command);
}
