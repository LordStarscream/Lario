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
 * Ruft `GET {hostUrl}/api/health` auf. Im Android-Paket läuft der
 * Abruf über das native HTTP-Plugin (CapacitorHttp, in
 * capacitor.config.ts aktiviert): `fetch` wird auf den nativen
 * Netzwerk-Stack umgeleitet, der die Regeln aus
 * `res/xml/network_security_config.xml` anwendet — kein
 * WebView-Mixed-Content-Problem und keine CORS-Einschränkung.
 * Zertifikatsverifizierung bleibt aktiv; bei HTTPS mit
 * self-signed Zertifikat muss das Zertifikat (oder dessen CA) auf
 * dem Gerät installiert sein (User-Truststore wird als
 * Vertrauensanker konfiguriert), sonst schlägt der Abruf mit einem
 * Fehler fehl, der hier sichtbar gemeldet wird
 * (Zertifikatsvertrauen — Schritt 2).
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
