/**
 * Minimale Ambient-Deklarationen für die `node:`-Module, die `schema.spec.ts`
 * für den ECHTEN-SQLite-Test via `node:sqlite` nutzt.
 *
 * Hintergrund: Der Angular-Build der Mobile-App (`ng test mobile`) führt
 * einen Type-Check über die App-Dateien durch, und die Mobile-App stellt
 * dafür keine Node-Typdefinitionen bereit (TS2591 „Cannot find name
 * 'node:fs'"). Diese Deklarationen schließen Lücke NUR für den
 * Build-Type-Check; zur Testlaufzeit (Vitest, node-Umfeld) kommen die
 * echten eingebauten `node:`-Module zum Einsatz.
 *
 * Bewusst klein gehalten: nur die APIs, die `schema.spec.ts` verwendet.
 */

declare module 'node:fs' {
  export function mkdtempSync(prefix: string): string;
  export function rmSync(
    path: string,
    options?: { recursive?: boolean; force?: boolean },
  ): void;
}

declare module 'node:os' {
  export function tmpdir(): string;
}

declare module 'node:path' {
  export function join(...paths: string[]): string;
}

declare module 'node:sqlite' {
  /** Zulässige Bind-Werte für `prepare(...).run()/all()/get()`. */
  export type SqliteValue = string | number | bigint | null;

  export class StatementSync {
    run(...params: SqliteValue[]): void;
    all(...params: SqliteValue[]): unknown[];
    get(...params: SqliteValue[]): unknown;
  }

  export class DatabaseSync {
    constructor(filename: string);
    /** Führt eine oder mehrere SQL-Anweisungen ohne Ergebnis zurück. */
    exec(sql: string): void;
    prepare(sql: string): StatementSync;
    close(): void;
  }
}
