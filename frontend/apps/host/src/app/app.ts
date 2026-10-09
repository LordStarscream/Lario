import { Component, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { LarioHostService } from '../generated/services/LarioHostService';
import type { HealthResponse } from '../generated/models/HealthResponse';
import type { VersionResponse } from '../generated/models/VersionResponse';

export interface HostApiState {
  health: HealthResponse;
  version: VersionResponse;
}

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  /**
   * Health- und Versionsantworten der Lario-Host-API, geladen bei Start
   * über den generierten OpenAPI-Client (frontend: generate:client).
   */
  readonly api = signal<HostApiState | null>(null);

  constructor() {
    Promise.all([LarioHostService.getHealth(), LarioHostService.getVersion()])
      .then(([health, version]) => this.api.set({ health, version }))
      .catch(() => this.api.set(null));
  }
}
