import { Injectable, inject } from '@angular/core';
import { LocalDatabase } from './local-database';
import { deleteMetaSet, selectMetaSet, upsertMetaSet } from './statements';

/**
 * Zugriffs-Schicht für die Meta-Tabelle (Schritt 8, Batch A).
 *
 * Meta-Daten sind flache key/value-Einträge (z. B. stabile Geräte-ID,
 * Host-URL, Sync-Revisionen). Keinerlei Finanzlogik — nur SQL-Statements
 * aus `statements.ts` über `LocalDatabase`.
 */
@Injectable({ providedIn: 'root' })
export class MetaService {
  // inject() statt Konstruktor-Injection (ESLint prefer-inject); die App
  // holt MetaService ohnehin aus dem DI-Container (providedIn: 'root').
  private readonly db = inject(LocalDatabase);

  /** Meta-Wert lesen; `null`, wenn der Key nicht (mehr) existiert. */
  async get(key: string): Promise<string | null> {
    const set = selectMetaSet(key);
    const rows = await this.db.query<Record<string, unknown>>(
      set.statement,
      set.values ?? [],
    );
    const value = rows[0]?.['value'];
    return typeof value === 'string' ? value : null;
  }

  /** Meta-Wert setzen (Upsert: ein bestehender Wert wird ersetzt). */
  async set(key: string, value: string): Promise<void> {
    await this.db.executeSet([upsertMetaSet(key, value)]);
  }

  /** Meta-Wert löschen (wirkt nichts, wenn der Key fehlt). */
  async delete(key: string): Promise<void> {
    await this.db.executeSet([deleteMetaSet(key)]);
  }
}
