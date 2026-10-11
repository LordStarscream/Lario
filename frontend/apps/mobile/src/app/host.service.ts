import { Injectable } from '@angular/core';

/** Ergebnis des Host-Verbindungstests. */
export interface HostCheckResult {
  ok: boolean;
  /** Menschlesbarer Status bzw. Fehlerhinweis (inkl. rohem Fehlerstring). */
  message: string;
}

/**
 * Verbindungstest zum Lario-Host über das Heimnetz (LAN).
 *
 * Ruft `GET {hostUrl}/api/health` auf. Im Android-WebView läuft der
 * Abruf über die Netzwerk-Stack des WebViews und nutzt den
 * System-Truststore: Bei HTTPS mit self-signed Zertifikat ist das
 * Zertifikat (oder dessen CA) im Gerätespeicher installiert, sonst
 * schlägt der Abruf mit einem Fehler fehl, der hier sichtbar gemeldet
 * wird (Zertifikatsvertrauen — Schritt 2).
 */
@Injectable({ providedIn: 'root' })
export class HostService {
  async checkHealth(hostUrl: string): Promise<HostCheckResult> {
    const url = hostUrl.trim().replace(/\/+$/, '') + '/api/health';
    try {
      const response = await fetch(url, { method: 'GET' });
      if (!response.ok) {
        return { ok: false, message: `HTTP ${response.status}` };
      }
      const body = await response.text();
      return { ok: true, message: body };
    } catch (error) {
      // Netzwerkfehler: Host nicht erreichbar (ausgeschaltet, anderes
      // Netz, Flugmodus) oder Zertifikat nicht vertrauenswürdig
      // (net::ERR_CERT_AUTHORITY_INVALID taucht als generischer
      // Fetch-Fehler auf).
      return {
        ok: false,
        message: `Nicht erreichbar (${String(error)})`,
      };
    }
  }
}
