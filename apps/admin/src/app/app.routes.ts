import { Routes } from '@angular/router';
import { adminGuard, loggedOutGuard } from './core/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [loggedOutGuard],
    loadComponent: () => import('./auth/login.component').then((m) => m.LoginComponent)
  },
  {
    path: '',
    canActivate: [adminGuard],
    loadComponent: () => import('./shell/admin-shell.component').then((m) => m.AdminShellComponent),
    children: [
      {
        path: '',
        pathMatch: 'full',
        redirectTo: 'documents'
      },
      {
        path: 'documents',
        loadComponent: () => import('./documents/documents.component').then((m) => m.DocumentsComponent)
      },
      {
        path: 'audit',
        loadComponent: () => import('./audit/audit.component').then((m) => m.AuditComponent)
      }
    ]
  },
  {
    path: '**',
    redirectTo: ''
  }
];
