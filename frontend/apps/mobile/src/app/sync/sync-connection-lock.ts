import { Injectable, signal } from '@angular/core';

/** Einrichtung und Synchronisation teilen die globale Client-Konfiguration. */
@Injectable({ providedIn: 'root' })
export class SyncConnectionLock {
  readonly busy = signal(false);

  tryAcquire(): boolean {
    if (this.busy()) return false;
    this.busy.set(true);
    return true;
  }

  release(): void {
    this.busy.set(false);
  }
}
