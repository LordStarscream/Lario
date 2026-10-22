import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../api/auth';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';

/**
 * Login (Design D1). Zustand (`error`, `busy`) sind Signale: Die App läuft
 * zoneless mit OnPush — eine Fehlerteilung nach `await` löst ohne Signal-
 * Schreiben keine Aktualisierung aus, die Fehlermeldung wäre erst nach dem
 * nächsten DOM-Event (z. B. Fokus im Benutzernamen-Feld) sichtbar.
 * (Nachtrag N2, Review 7a.)
 */
@Component({
  selector: 'app-login',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './login.html',
  styleUrl: './login.css',
})
export class Login {
  username = '';
  password = '';
  readonly error = signal<ApiErrorInfo | null>(null);
  readonly busy = signal(false);

  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly errors = inject(ApiErrorHandler);

  async submit(): Promise<void> {
    if (this.busy() || this.username.trim() === '' || this.password === '') {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.login(this.username.trim(), this.password);
      await this.router.navigate(['/']);
    } catch (error) {
      // Login-Fehler selbst behandeln: kein 401-Redirect hier (ApiErrorHandler
      // würde bei 401 auf /login loopen — daher toApiErrorInfo via handle:
      // 401-Redirect wird vom Handler unterdrückt, weil die URL /auth/login enthält).
      this.error.set(this.errors.handle(error));
      this.busy.set(false);
    }
  }
}
