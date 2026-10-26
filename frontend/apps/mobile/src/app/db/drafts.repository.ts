import { Injectable, inject } from '@angular/core';
import {
  DELETE_DRAFT,
  SELECT_DRAFTS,
  SELECT_DRAFT_BY_ID,
  UPSERT_DRAFT,
} from './statements';
import { LocalDatabase } from './local-database';

/**
 * Entwurf einer Ausgabeeingabe (Formular-Zustand, noch KEINE Buchung).
 * `data` ist der serialisierte Formular-Zustand (JSON, Batch D).
 */
export interface LocalDraft {
  id: string;
  name: string;
  data: string;
  createdAtUtc: string;
  updatedAtUtc: string;
}

/**
 * Entwurfs-Repository (Schritt 8, Batch B, B7): Entwürfe werden
 * unabhängig von Buchung/Outbox gespeichert.
 *
 * - `save`: Upsert — ein bestehender Entwurf wird ersetzt,
 *   `createdAtUtc` bleibt erhalten (nur name/data/updated_at werden
 *   gesetzt).
 * - `get`/`list`/`delete` für das Formular (Batch D).
 * - `now` ist immer ein Parameter (Testbarkeit, keine versteckte Uhr).
 */
@Injectable({ providedIn: 'root' })
export class DraftsRepository {
  private readonly db = inject(LocalDatabase);

  async save(id: string, name: string, data: string, now: Date): Promise<void> {
    const nowIso = now.toISOString();
    await this.db.executeSet([
      { statement: UPSERT_DRAFT, values: [id, name, data, nowIso, nowIso] },
    ]);
  }

  async get(id: string): Promise<LocalDraft | null> {
    const rows = await this.db.query<Record<string, unknown>>(
      SELECT_DRAFT_BY_ID,
      [id],
    );
    return rows.length === 0 ? null : mapDraftRow(rows[0]);
  }

  async list(): Promise<LocalDraft[]> {
    const rows = await this.db.query<Record<string, unknown>>(
      SELECT_DRAFTS,
      [],
    );
    return rows.map((row) => mapDraftRow(row));
  }

  async delete(id: string): Promise<void> {
    await this.db.executeSet([{ statement: DELETE_DRAFT, values: [id] }]);
  }
}

function mapDraftRow(row: Record<string, unknown>): LocalDraft {
  return {
    id: String(row['id']),
    name: String(row['name']),
    data: String(row['data']),
    createdAtUtc: String(row['created_at_utc']),
    updatedAtUtc: String(row['updated_at_utc']),
  };
}
