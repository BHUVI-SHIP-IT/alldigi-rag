import { ApplicationConfig, importProvidersFrom } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { BookOpenCheck, LogIn, LogOut, LucideAngularModule, MessageSquareLock, Send, ShieldCheck, Square } from 'lucide-angular';
import { authInterceptor } from './core/auth.interceptor';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([authInterceptor])),
    importProvidersFrom(
      LucideAngularModule.pick({
        BookOpenCheck,
        LogIn,
        LogOut,
        MessageSquareLock,
        Send,
        ShieldCheck,
        Square
      })
    )
  ]
};
