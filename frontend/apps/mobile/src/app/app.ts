import { Component, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';
import { PrototypeEntry, SqliteService } from './sqlite.service';
import { HostCheckResult, HostService } from './host.service';

/**
 * Prototyp-Oberfläche (Schritt 2): lokaler Testdatensatz in SQLite,
 * stabile Geräte-ID (Kopplungsgrundlage) und LAN-Verbindungstest zum
 * Host. Keine produktive Finanzberechnung.
 */
@Component({
  selector: 'app-root',
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly sqlite = inject(SqliteService);
  private readonly host = inject(HostService);

  /** true im Android-WebView; false im Browser (ng serve, Tests). */
  readonly isNative = this.sqlite.isNative;

  initError: string | null = null;
  deviceId: string | null = null;

  entries: PrototypeEntry[] = [];
  label = '';
  amountInput = '';
  saving = false;
  saveError: string | null = null;

  hostUrl = 'http://192.168.1.226:8080';
  checking = false;
  hostCheck: HostCheckResult | null = null;

  async ngOnInit(): Promise<void> {
    if (!this.isNative) {
      return;
    }
    try {
      await this.sqlite.init();
      this.deviceId = await this.sqlite.getDeviceId();
      this.entries = await this.sqlite.listEntries();
      const storedUrl = await this.sqlite.getMeta('host_url');
      if (storedUrl) {
        this.hostUrl = storedUrl;
      }
    } catch (error) {
      this.initError = String(error);
    }
  }

  /** Testdatensatz lokal speichern — funktioniert ohne Host und offline. */
  async save(): Promise<void> {
    this.saveError = null;
    const trimmedLabel = this.label.trim();
    if (trimmedLabel === '') {
      this.saveError = 'Bitte eine Bezeichnung eingeben.';
      return;
    }
    const amountCents = this.parseAmount();
    if (amountCents === undefined) {
      this.saveError = 'Betrag unzulässig: Zahl in Euro mit maximal zwei Nachkommastellen.';
      return;
    }
    if (!this.isNative || !this.sqlite.ready) {
      this.saveError =
        'Lokaler Speicher ist nur im Android-Paket verfügbar (dies ist die Browser-Vorschau).';
      return;
    }
    this.saving = true;
    try {
      const entry = await this.sqlite.saveEntry(trimmedLabel, amountCents);
      this.entries = [entry, ...this.entries];
      this.label = '';
      this.amountInput = '';
    } catch (error) {
      this.saveError = String(error);
    } finally {
      this.saving = false;
    }
  }

  /** Verbindung zum Host testen und Host-URL lokal merken. */
  async testHost(): Promise<void> {
    this.checking = true;
    this.hostCheck = null;
    try {
      const url = this.hostUrl.trim();
      this.hostCheck = await this.host.checkHealth(url);
      if (this.sqlite.ready && url !== '') {
        await this.sqlite.setMeta('host_url', url);
      }
    } finally {
      this.checking = false;
    }
  }

  /**
   * Technische Euro→Cent-Übersetzung des Prototyp-Eingangs
   * (nur ganzzahlige Operationen, keine Finanzlogik).
   * Liefert null (kein Betrag), einen Cent-Wert oder undefined (ungültig).
   */
  private parseAmount(): number | null | undefined {
    const raw = this.amountInput.trim();
    if (raw === '') {
      return null;
    }
    const match = /^(\d{1,10})(?:[.,](\d{1,2}))?$/.exec(raw);
    if (!match) {
      return undefined;
    }
    const euros = Number(match[1]);
    const decimals = (match[2] ?? '').padEnd(2, '0');
    return euros * 100 + Number(decimals);
  }

  /** Cent-Wert für die Anzeige formatieren (nur ganzzahlige Operationen). */
  formatCents(cents: number | null): string {
    if (cents === null) {
      return '';
    }
    const sign = cents < 0 ? '-' : '';
    const abs = Math.abs(cents);
    return `${sign}${Math.floor(abs / 100)},${String(abs % 100).padStart(2, '0')} €`;
  }
}
