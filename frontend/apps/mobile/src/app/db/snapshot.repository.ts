import { Injectable, inject } from '@angular/core';
import {
  LocalDatabase,
  type LocalDatabaseReadHandle,
} from './local-database';
import {
  DELETE_SNAPSHOT_ACCOUNTS,
  DELETE_SNAPSHOT_CATEGORIES,
  DELETE_SNAPSHOT_POOLS,
  DELETE_SNAPSHOT_TAX_CATEGORIES,
  INSERT_SNAPSHOT_ACCOUNT,
  INSERT_SNAPSHOT_CATEGORY,
  INSERT_SNAPSHOT_POOL,
  INSERT_SNAPSHOT_TAX_CATEGORY,
  SELECT_ACCOUNT_STATUSES,
  SELECT_CATEGORY_STATUSES,
  SELECT_POOL_STATUSES,
  SELECT_SNAPSHOT_ACCOUNTS,
  SELECT_SNAPSHOT_CATEGORIES,
  SELECT_SNAPSHOT_POOLS,
  SELECT_SNAPSHOT_TAX_CATEGORIES,
  SELECT_TAX_CATEGORY_STATUSES,
  selectMetaSet,
  upsertMetaSet,
  type SqlSet,
} from './statements';
import type {
  AccountDto,
  CategoryDto,
  PoolDto,
  TaxCategoryDto,
} from '../../generated';

/**
 * Stammdaten-Snapshot der lokalen Offline-Erfassung (Schritt 8, Batch C,
 * Design D3).
 *
 * Der Snapshot ist eine vollständige Kopie der Host-Stammdaten
 * (Konten, Pools, Kategorien, Steuerkategorien) auf dem Gerät. Er wird
 * beim Verbinden (Setup) vollständig ersetzt: ALLE vier Tabellen in
 * EINER atomaren `executeSet`-Transaktion (4×DELETE + alle INSERT +
 * Zeitstempel-Meta) — all-or-nothing, kein gemischter Zustand aus zwei
 * Ladeversuchen.
 *
 * Konsistente Lesungen (P2, Batch-C-Review): `loadSnapshot` und
 * `referenceCatalog` lesen Zeitstempel UND die vier Tabellen in EINER
 * `withSerializedRead`-Sektion (Connection-Operations-Kette): ein
 * paralleles `replace` ist entweder vollständig vor oder nach der
 * Lese-Sektion committet — die Lese liefert niemals einen gemischten
 * alten/neuen Stand, der als gespeichert nie existierte.
 *
 * Der Snapshot dient (1) der Referenzvalidierung lokaler Ausgaben
 * (Buchung muss gegen `active` Stammdaten referenzieren) und
 * (2) der Offline-Auswahl in den Formularfeldern (Batch E). Er enthält
 * KEINE Salden und ist keine Saldenquelle.
 */

/** Stammdaten-Snapshot im Speicherformat (id → DTO). */
export interface MasterDataSnapshot {
  accounts: Map<string, AccountDto>;
  pools: Map<string, PoolDto>;
  categories: Map<string, CategoryDto>;
  taxCategories: Map<string, TaxCategoryDto>;
}

/** id → status je Stammdaten-Tabelle (Referenzvalidierung, Design D7). */
export interface ReferenceCatalog {
  accounts: Map<string, string>;
  pools: Map<string, string>;
  categories: Map<string, string>;
  taxCategories: Map<string, string>;
}

@Injectable({ providedIn: 'root' })
export class SnapshotRepository {
  /** Meta-Key des Snapshot-Zeitstempels (UTC-ISO). */
  static readonly FETCHED_AT_META_KEY = 'snapshot.fetched_at';

  // inject() statt Constructor-Parameter (ESLint prefer-inject).
  private readonly db = inject(LocalDatabase);

  /**
   * Snapshot vollständig ersetzen (Design D3): 4×DELETE, dann alle
   * INSERT, dann Zeitstempel — in EINER atomaren executeSet-Transaktion.
   * Leere Maps sind erlaubt (gültiger leerer Snapshot).
   */
  async replace(
    snapshot: MasterDataSnapshot,
    fetchedAtUtc: string,
  ): Promise<void> {
    // Ein Batch (atomar): alle Zeilen oder keine (Design D3).
    await this.db.executeSet(this.buildReplaceSets(snapshot, fetchedAtUtc));
  }

  /**
   * SQL-Sets für ein vollständiges Snapshot-Replace bauen (4×DELETE,
   * alle INSERT, Zeitstempel-Meta) — rein, ohne Datenbankzugriff.
   * (P2, Batch-C-Review) Die Setup-Component kombiniert diese Sets mit
   * der auth-Meta in EINER lokalen Transaktion, damit Snapshot und
   * Verbindungsdaten nie getrennt committet werden; `replace` nutzt den
   * selben Baukasten als alleinigen Batch.
   */
  buildReplaceSets(
    snapshot: MasterDataSnapshot,
    fetchedAtUtc: string,
  ): SqlSet[] {
    const sets: SqlSet[] = [
      { statement: DELETE_SNAPSHOT_ACCOUNTS, values: [] },
      { statement: DELETE_SNAPSHOT_POOLS, values: [] },
      { statement: DELETE_SNAPSHOT_CATEGORIES, values: [] },
      { statement: DELETE_SNAPSHOT_TAX_CATEGORIES, values: [] },
    ];
    for (const account of snapshot.accounts.values()) {
      sets.push({
        statement: INSERT_SNAPSHOT_ACCOUNT,
        values: [
          account.id,
          account.name,
          account.type,
          account.isInterestBearing ? 1 : 0,
          account.status,
          Number(account.baseRevision),
        ],
      });
    }
    for (const pool of snapshot.pools.values()) {
      sets.push({
        statement: INSERT_SNAPSHOT_POOL,
        values: [pool.id, pool.name, pool.status, Number(pool.baseRevision)],
      });
    }
    for (const category of snapshot.categories.values()) {
      sets.push({
        statement: INSERT_SNAPSHOT_CATEGORY,
        values: [
          category.id,
          category.name,
          category.parentId,
          category.status,
          Number(category.baseRevision),
          category.taxPreset === null
            ? null
            : JSON.stringify(category.taxPreset),
        ],
      });
    }
    for (const taxCategory of snapshot.taxCategories.values()) {
      sets.push({
        statement: INSERT_SNAPSHOT_TAX_CATEGORY,
        values: [
          taxCategory.id,
          taxCategory.name,
          taxCategory.parentId,
          taxCategory.status,
          Number(taxCategory.baseRevision),
        ],
      });
    }
    sets.push(
      upsertMetaSet(SnapshotRepository.FETCHED_AT_META_KEY, fetchedAtUtc),
    );
    return sets;
  }

  /**
   * Gespeicherten Snapshot laden; `null`, wenn noch kein Snapshot geladen
   * wurde (kein Zeitstempel-Meta). Leere Tabellen sind ein gültiger
   * (leerer) Snapshot.
   *
   * Zeitstempel UND Tabellen in EINER serialisierten Lese-Sektion lesen
   * (P2, Batch-C-Review): kein `replace` kann zwischen den Queries
   * committen — das Ergebnis ist entweder der vollständig alte oder der
   * vollständig neue Stand (Klassen-Doku).
   */
  async loadSnapshot(): Promise<MasterDataSnapshot | null> {
    return this.db.withSerializedRead(async (db) => {
      const fetchedAt = await this.readFetchedAt(db);
      if (fetchedAt === null) {
        return null;
      }
      const accountRows = await db.query<Record<string, unknown>>(
        SELECT_SNAPSHOT_ACCOUNTS,
        [],
      );
      const poolRows = await db.query<Record<string, unknown>>(
        SELECT_SNAPSHOT_POOLS,
        [],
      );
      const categoryRows = await db.query<Record<string, unknown>>(
        SELECT_SNAPSHOT_CATEGORIES,
        [],
      );
      const taxCategoryRows = await db.query<Record<string, unknown>>(
        SELECT_SNAPSHOT_TAX_CATEGORIES,
        [],
      );
      return {
        accounts: new Map(
          accountRows.map((row) => [String(row['id']), mapAccountRow(row)]),
        ),
        pools: new Map(
          poolRows.map((row) => [String(row['id']), mapPoolRow(row)]),
        ),
        categories: new Map(
          categoryRows.map((row) => [
            String(row['id']),
            mapCategoryRow(row),
          ]),
        ),
        taxCategories: new Map(
          taxCategoryRows.map((row) => [
            String(row['id']),
            mapTaxCategoryRow(row),
          ]),
        ),
      };
    });
  }

  /**
   * Existenz-/Status-Katalog für die Referenzvalidierung; `null`, wenn
   * noch kein Snapshot geladen wurde (kein Zeitstempel-Meta). Leere
   * Maps sind erlaubt (leerer Snapshot → jede Referenz unbekannt).
   *
   * Wie `loadSnapshot` in einer serialisierten Lese-Sektion: die
   * Referenzvalidierung prüft nie gegen einen gemischten Katalog
   * (P2, Batch-C-Review).
   */
  async referenceCatalog(): Promise<ReferenceCatalog | null> {
    return this.db.withSerializedRead(async (db) => {
      if ((await this.readFetchedAt(db)) === null) {
        return null;
      }
      const accounts = await db.query<Record<string, unknown>>(
        SELECT_ACCOUNT_STATUSES,
        [],
      );
      const pools = await db.query<Record<string, unknown>>(
        SELECT_POOL_STATUSES,
        [],
      );
      const categories = await db.query<Record<string, unknown>>(
        SELECT_CATEGORY_STATUSES,
        [],
      );
      const taxCategories = await db.query<Record<string, unknown>>(
        SELECT_TAX_CATEGORY_STATUSES,
        [],
      );
      const toStatusMap = (rows: Record<string, unknown>[]): Map<string, string> =>
        new Map(rows.map((row) => [String(row['id']), String(row['status'])]));
      return {
        accounts: toStatusMap(accounts),
        pools: toStatusMap(pools),
        categories: toStatusMap(categories),
        taxCategories: toStatusMap(taxCategories),
      };
    });
  }

  /**
   * Zeitstempel-Meta innerhalb einer Lese-Sektion lesen (Handle-Query,
   * keine Operationen-Kette — P2, Batch-C-Review).
   */
  private async readFetchedAt(
    db: LocalDatabaseReadHandle,
  ): Promise<string | null> {
    const set = selectMetaSet(SnapshotRepository.FETCHED_AT_META_KEY);
    const rows = await db.query<Record<string, unknown>>(
      set.statement,
      set.values ?? [],
    );
    const value = rows[0]?.['value'];
    return typeof value === 'string' ? value : null;
  }

  /**
   * Snapshot-Zeitstempel (UTC-ISO) lesen; `null`, wenn noch kein
   * Snapshot geladen wurde.
   */
  async fetchedAt(): Promise<string | null> {
    const set = selectMetaSet(SnapshotRepository.FETCHED_AT_META_KEY);
    const rows = await this.db.query<Record<string, unknown>>(
      set.statement,
      set.values ?? [],
    );
    const value = rows[0]?.['value'];
    return typeof value === 'string' ? value : null;
  }
}

/** Account-Zeile (snake_case) → AccountDto. */
function mapAccountRow(row: Record<string, unknown>): AccountDto {
  return {
    id: String(row['id']),
    name: String(row['name']),
    type: String(row['type']),
    isInterestBearing: Number(row['is_interest_bearing']) === 1,
    status: String(row['status']),
    baseRevision: Number(row['base_revision']),
  };
}

/** Pool-Zeile (snake_case) → PoolDto. */
function mapPoolRow(row: Record<string, unknown>): PoolDto {
  return {
    id: String(row['id']),
    name: String(row['name']),
    status: String(row['status']),
    baseRevision: Number(row['base_revision']),
  };
}

/** Kategorie-Zeile (snake_case) → CategoryDto (taxPreset aus JSON). */
function mapCategoryRow(row: Record<string, unknown>): CategoryDto {
  const presetJson = row['tax_preset_json'];
  return {
    id: String(row['id']),
    name: String(row['name']),
    parentId:
      row['parent_id'] === null || row['parent_id'] === undefined
        ? null
        : String(row['parent_id']),
    status: String(row['status']),
    baseRevision: Number(row['base_revision']),
    taxPreset:
      presetJson === null || presetJson === undefined
        ? null
        : (JSON.parse(String(presetJson)) as CategoryDto['taxPreset']),
  };
}

/** Steuerkategorie-Zeile (snake_case) → TaxCategoryDto. */
function mapTaxCategoryRow(row: Record<string, unknown>): TaxCategoryDto {
  return {
    id: String(row['id']),
    name: String(row['name']),
    parentId:
      row['parent_id'] === null || row['parent_id'] === undefined
        ? null
        : String(row['parent_id']),
    status: String(row['status']),
    baseRevision: Number(row['base_revision']),
  };
}
