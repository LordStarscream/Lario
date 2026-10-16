import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../api/auth';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';

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
  error: ApiErrorInfo | null = null;
  busy = false;

  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly errors = inject(ApiErrorHandler);

  async submit(): Promise<void> {
    if (this.busy || this.username.trim() === '' || this.password === '') {
      return;
    }
    this.busy = true;
    this.error = null;
    try {
      await this.auth.login(this.username.trim(), this.password);
      await this.router.navigate(['/']);
    } catch (error) {
      // Login-Fehler selbst behandeln: kein 401-Redirect hier (ApiErrorHandler
      // würde bei 401 auf /login loopen — daher toApiErrorInfo via handle:
      // 401-Redirect wird vom Handler unterdrückt, weil die URL /auth/login enthält).
      this.error = this.errors.handle(error);
      this.busy = false;
    }
  }
}
