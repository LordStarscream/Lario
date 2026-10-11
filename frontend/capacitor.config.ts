import type { CapacitorConfig } from '@capacitor/cli';

/**
 * Capacitor-Konfiguration für die Lario-Android-App (Schritt 2 Prototyp).
 *
 * webDir ist der Build-Output des Angular-Projekts "mobile"
 * (`npm run build:mobile` erzeugt frontend/dist/mobile/browser). Der
 * Host-Build (dist/host) ist NICHT Teil des Android-Pakets.
 *
 * CapacitorHttp: `window.fetch` wird im WebView auf den nativen HTTP-
 * Stack umgeleitet. Damit läuft der LAN-Hostabruf (/api/health) über
 * die nativen Netzwerkregeln (network_security_config.xml) —
 * kein WebView-Mixed-Content-Problem (HTTPS-Seite + HTTP-Host),
 * keine CORS-Einschränkung. Zertifikatsverifizierung bleibt aktiv;
 * der User-Truststore (installiertes Test-Zertifikat) wird durch die
 * network_security_config anerkannt. Im Browser (ng serve, Tests) ist
 * das Plugin inaktiv und `fetch` bleibt der reguläre Web-Fetch.
 */
const config: CapacitorConfig = {
  appId: 'de.lario.app',
  appName: 'Lario',
  webDir: 'dist/mobile/browser',
  plugins: {
    CapacitorHttp: {
      enabled: true,
    },
  },
};

export default config;
