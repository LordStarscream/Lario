import type { CapacitorConfig } from '@capacitor/cli';

/**
 * Capacitor-Konfiguration für die Lario-Android-App (Schritt 2 Prototyp).
 *
 * webDir ist der Build-Output des Angular-Projekts "mobile"
 * (`npm run build:mobile` erzeugt frontend/dist/mobile/browser). Der
 * Host-Build (dist/host) ist NICHT Teil des Android-Pakets.
 */
const config: CapacitorConfig = {
  appId: 'de.lario.app',
  appName: 'Lario',
  webDir: 'dist/mobile/browser',
};

export default config;
