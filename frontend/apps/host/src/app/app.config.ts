import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { OpenAPI } from '../generated';

// Die Host-Weboberfläche wird vom Host aus demselben Origin ausgeliefert
// (statische wwwroot). Leere Basisadresse: alle API-Anfragen des generierten
// Clients sind relativ zum aktuellen Origin — unabhängig von Port/Rechner.
// Im Angular-Devserver übernimmt proxy.conf.json die Weiterleitung /api → Host.
// Der Standardwert im generierten Code (http://localhost:8080) wird bewusst
// hier überschrieben, damit die Generierung (generate:client) unverändert bleibt.
OpenAPI.BASE = '';

// Token-Authentifizierung (Design D1): der generierte Fetch-Client hängt
// das Bearer-Token automatisch an, sobald im localStorage ein Token
// liegt. Ein 401 wird zentral von ApiErrorHandler behandelt (Login-Redirect).
OpenAPI.TOKEN = async () => {
  try {
    return localStorage.getItem('lario.apiToken') ?? '';
  } catch {
    return '';
  }
};

export const appConfig: ApplicationConfig = {
  providers: [provideBrowserGlobalErrorListeners(), provideRouter(routes)],
};
