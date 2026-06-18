import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

export const employeeGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isEmployee()) {
    return true;
  }

  return router.createUrlTree(['/login']);
};

export const loggedOutGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isEmployee() ? router.createUrlTree(['/']) : true;
};
