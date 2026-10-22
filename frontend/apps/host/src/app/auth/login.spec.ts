import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { Login } from './login';
import { AuthService } from '../../api/auth';
import { ApiError as ApiErrorClass } from '../../generated/core/ApiError';
import type { ApiRequestOptions } from '../../generated/core/ApiRequestOptions';
import type { ApiResult } from '../../generated/core/ApiResult';
import type { ApiErrorBody } from '../../generated/models/ApiErrorBody';

/**
 * Nachtrag N2 (Review 7a): Login-Fehler blieben unsichtbar, bis der Nutzer
 * in ein Feld klickte. Ursache: zoneless App + OnPush + `error`/`busy` als
 * einfache Properties — nach dem `await` (verwerfener Login) schreib die
 * Komponente kein Signal, es wurde keine Aktualisierung geplant; erst das
 * nächste DOM-Event (Fokus/Tippen) löste die Change Detection aus.
 *
 * Regressionstest: gerendertes Template, fehlgeschlagene Anmeldung (401),
 * danach NUR auf das automatisierte (zoneless) Refresh warten — kein
 * `detectChanges()` nach dem Submit und keine künstlichen DOM-Events.
 * Banner und Button-Zustand müssen sich allein durch die Signal-Schreiben
 * in `submit()` aktualisieren.
 */

function login401(): ApiErrorClass {
  const request = {
    method: 'POST',
    url: '/api/v1/auth/login',
  } as unknown as ApiRequestOptions;
  const body = {
    error: {
      code: 'invalid_credentials',
      message: 'Ungültiger Benutzername oder ungültiges Passwort.',
    } satisfies ApiErrorBody,
  };
  const response = {
    url: '/api/v1/auth/login',
    status: 401,
    statusText: 'Unauthorized',
    body,
  } as unknown as ApiResult;
  return new ApiErrorClass(request, response, 'Unauthorized');
}

function createRenderedLogin(login: (u: string, p: string) => Promise<void>) {
  const navigate = vi.fn().mockResolvedValue(true);
  const navigateByUrl = vi.fn().mockResolvedValue(true);
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [Login],
    providers: [
      { provide: AuthService, useValue: { login: vi.fn().mockImplementation(login) } },
      { provide: Router, useValue: { navigate, navigateByUrl } },
    ],
  });
  const fixture: ComponentFixture<Login> = TestBed.createComponent(Login);
  fixture.detectChanges(); // nur der initiale Render — danach kein manuelles detectChanges mehr
  return {
    component: fixture.componentInstance,
    fixture,
    el: fixture.nativeElement as HTMLElement,
    navigate,
  };
}

function submitButton(el: HTMLElement): HTMLButtonElement {
  return el.querySelector('button[type="submit"]') as HTMLButtonElement;
}

describe('Login — Fehleranzeige ohne weiteres DOM-Event (Nachtrag N2)', () => {
  it('zeigt 401-Fehler an und gibt den Button frei, allein über das Signal-Refresh', async () => {
    const { component, fixture, el } = createRenderedLogin(() => Promise.reject(login401()));

    component.username = 'mario';
    component.password = 'falsch';
    fixture.detectChanges(); // Eingaben rendern (ngModel) — der letzte manuelle Render

    const p = component.submit();
    await p; // verwerfene Anmeldung — ab hier kein detectChanges, kein DOM-Event

    // Das zoneless Refresh (Signal-Write in catch) muss Banner + Button
    // aktualisieren. vi.waitFor wartet nur auf Zeit, ohne selbst zu rendern.
    await vi.waitFor(() => {
      const banner = el.querySelector('.banner-error');
      expect(banner).not.toBeNull();
      expect(banner?.textContent).toContain('Anmeldung fehlgeschlagen');
      expect(banner?.textContent).toContain(
        'Ungültiger Benutzername oder ungültiges Passwort.',
      );
    });

    const button = submitButton(el);
    expect(button.disabled).toBe(false);
    expect(button.textContent).toContain('Anmelden');
    expect(component.busy()).toBe(false);
    expect(component.error()?.code).toBe('invalid_credentials');
  });

  it('hält den Button während des Login-Verkehrs gesperrt und navigiert bei Erfolg', async () => {
    let resolveLogin!: () => void;
    const { component, el, navigate } = createRenderedLogin(
      () =>
        new Promise<void>((resolve) => {
          resolveLogin = resolve;
        }),
    );

    component.username = 'mario';
    component.password = 'richtig';

    const p = component.submit();
    await vi.waitFor(() => {
      const button = submitButton(el);
      expect(button.disabled).toBe(true);
      expect(button.textContent).toContain('Anmeldung läuft');
    });

    resolveLogin();
    await p;
    expect(navigate).toHaveBeenCalledWith(['/']);
  });
});
