import { Routes } from '@angular/router';
import { employeeGuard, loggedOutGuard } from './core/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [loggedOutGuard],
    loadComponent: () => import('./auth/login.component').then((m) => m.LoginComponent)
  },
  {
    path: '',
    canActivate: [employeeGuard],
    loadComponent: () => import('./chat/chat.component').then((m) => m.ChatComponent)
  },
  {
    path: '**',
    redirectTo: ''
  }
];
