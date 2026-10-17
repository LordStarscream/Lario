import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../../api/auth';

/**
 * Schützt alle Seiten hinter dem Login: ohne gültiges Token
 * Weiterleitung auf /login (Design D1).
 */
export const authGuard: CanActivateFn = () => {
  const router = inject(Router);
  return AuthService.token() !== null || router.createUrlTree(['/login']);
};
