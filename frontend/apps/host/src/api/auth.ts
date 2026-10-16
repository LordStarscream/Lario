import { Injectable, signal } from '@angular/core';
import { LarioHostService } from '../generated/services/LarioHostService';
import type { LoginRequest } from '../generated/models/LoginRequest';

const TOKEN_KEY = 'lario.apiToken';
const USER_KEY = 'lario.user';

/**
 * Token-Authentifizierung gegen den Lario-Host (Design D1).
 * Das Token wird in localStorage gehalten und von der
 * HttpInterceptor an /api/v1-Requests angehängt; ein 401
 * führt zur Rückkehr auf den Login.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  /** Anzeigename des eingeloggten Users (lokal gemerkt). */
  readonly user = signal<string | null>(readUser());

  static token(): string | null {
    try {
      return localStorage.getItem(TOKEN_KEY);
    } catch {
      return null;
    }
  }

  get isAuthenticated(): boolean {
    return AuthService.token() !== null;
  }

  async login(username: string, password: string): Promise<void> {
    const request: LoginRequest = { username, password };
    const response = await LarioHostService.authLogin(request);
    try {
      localStorage.setItem(TOKEN_KEY, response.token);
      localStorage.setItem(USER_KEY, username);
    } catch {
      // localStorage nicht verfügbar: Session läuft ohne Persistenz.
    }
    this.user.set(username);
  }

  /** Logout beim Host; lokale Session wird unabhängig davon gelöscht. */
  async logout(): Promise<void> {
    try {
      await LarioHostService.authLogout();
    } catch {
      // Token möglicherweise bereits abgelaufen — lokal trotzdem beenden.
    }
    clearSession();
  }
}

/** Löscht Token und User ohne weiteren Request (z. B. nach 401). */
export function clearSession(): void {
  try {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
  } catch {
    // ignorieren
  }
}

function readUser(): string | null {
  try {
    return localStorage.getItem(USER_KEY);
  } catch {
    return null;
  }
}
