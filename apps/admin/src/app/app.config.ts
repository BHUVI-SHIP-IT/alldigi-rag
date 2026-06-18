import { ApplicationConfig, importProvidersFrom } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { ClipboardList, FileStack, FileUp, LockKeyhole, LogIn, LogOut, LucideAngularModule, RefreshCw, Search, ShieldCheck, UploadCloud } from 'lucide-angular';
import { authInterceptor } from './core/auth.interceptor';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([authInterceptor])),
    importProvidersFrom(
      LucideAngularModule.pick({
        ClipboardList,
        FileStack,
        FileUp,
        LockKeyhole,
        LogIn,
        LogOut,
        RefreshCw,
        Search,
        ShieldCheck,
        UploadCloud
      })
    )
  ]
};
