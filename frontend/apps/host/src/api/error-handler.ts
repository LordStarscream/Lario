import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { ApiError as ApiErrorClass } from '../generated/core/ApiError';
import { toApiErrorInfo, type ApiErrorInfo } from './api-error';
import { clearSession } from './auth';

/**
 * Zentrale Fehlerbehandlung für den generierten Fetch-Client:
 * normalisiert den Fehler und behandelt 401-antworten, indem die
 * lokale Session gelöscht und zum Login weitergeleitet wird
 * (Design D1 — der generierte Client läuft über fetch und wird
 * daher nicht von HttpInterceptors erfasst).
 */
@Injectable({ providedIn: 'root' })
export class ApiErrorHandler {
  private readonly router = inject(Router);

  handle(error: unknown): ApiErrorInfo {
    const info = toApiErrorInfo(error);
    const unauthorized =
      error instanceof ApiErrorClass &&
      error.status === 401 &&
      !error.url?.includes('/auth/login');
    if (unauthorized) {
      clearSession();
      void this.router.navigateByUrl('/login');
    }
    return info;
  }
}
