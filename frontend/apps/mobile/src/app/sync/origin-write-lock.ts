import { Injectable } from '@angular/core';

/**
 * Herkunfts-Schreibschutz (Schritt 9, Review-Befund P1, 19.11.2026).
 *
 * Serialisiert die gesamte lokale Buchungsanlage
 * (`BookingsRepository.save`: Referenzvalidierung gegen den Snapshot,
 * Zähler-/Geräte-ID-Lesung, finaler Schreib-Batch) mit dem
 * verbindungsändernden Setup-Commit (Herkunfts-Neuprüfung +
 * atomares `executeSet`-Commit). Damit gilt:
 *
 * 1. Zwischen der Herkunftsprüfung („keine lokalen Eingaben“) und dem
 *    Verbindungs-Commit (Host-/Snapshot-Wechsel) kann keine neue lokale
 *    Buchung entstehen — eine neu hinzugekommene Buchung unter dem
 *    alten Host verhindert den Commit (Herkunft bleibt eindeutig A).
 * 2. Ein unter dem alten Host gestarteter `save()` läuft unter der
 *    alten Validierungsbasis zu Ende; der verbindungsändernde Commit
 *    wartet auf ihn und prüft die Herkunft dann neu — die Anlage
 *    kann nicht „mit alter Validierungsbasis still“ in die neue
 *    Hostbindung landen.
 *
 * Ein zweites `listPending()` außerhalb der finalen Sektion reicht
 * nicht (Fenster zwischen Check und Write), ein reiner Button-Guard
 * ebenso nicht (Tabwechsel in die Offline-Erfassung während des
 * Setup-Netzwerklaufs). Die Sperre ist der Code-Level-Schutz; die
 * frühe Herkunftsprüfung vor dem Pairing bleibt als schnelle
 * Benutzerfeedback-Stufe bestehen.
 *
 * Abgrenzung `SyncConnectionLock`: Diese schützt die globale
 * Client-Konfiguration (BASE/TOKEN) über Setup/Health/Sync. Diese
 * Sperre schützt die Konsistenz von Herkunftsprüfung und
 * Schreib-Commits über Buchungsanlage und Setup-Commit. Beide werden
 * unabhängig gehalten (keine gemeinsame Warteschlange).
 *
 * Umsetzung: Promise-Kette im Muster von `LocalDatabase.serialize`
 * und `BookingsRepository.saveChain` — Abschnitte laufen strikt
 * nacheinander (FIFO), ein Fehler bricht die Kette nicht, spätere
 * Abschnitte starten weiterhin.
 */
@Injectable({ providedIn: 'root' })
export class OriginWriteLock {
  private chain: Promise<void> = Promise.resolve();

  /**
   * `section` exklusiv ausführen. Parallele Aufrufe warten in
   * Aufrufreihenfolge; das Ergebnis/Fehler der Sektion wird an den
   * jeweiligen Aufrufer weitergegeben.
   */
  run<T>(section: () => Promise<T> | T): Promise<T> {
    const started = this.chain.then(section, section);
    this.chain = started.then(
      () => undefined,
      () => undefined,
    );
    return started;
  }
}
