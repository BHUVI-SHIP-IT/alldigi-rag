import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, tap } from 'rxjs';
import { environment } from './environment';
import { LoginRequest, LoginResponse, Role } from './models';

const TOKEN_KEY = 'rag_employee_token';
const ROLE_KEY = 'rag_employee_role';
const EMAIL_KEY = 'rag_employee_email';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly tokenState = signal<string | null>(localStorage.getItem(TOKEN_KEY));
  private readonly roleState = signal<Role | null>((localStorage.getItem(ROLE_KEY) as Role | null) ?? null);
  private readonly emailState = signal<string | null>(localStorage.getItem(EMAIL_KEY));

  readonly token = computed(() => this.tokenState());
  readonly role = computed(() => this.roleState());
  readonly email = computed(() => this.emailState());
  readonly isEmployee = computed(() => this.roleState() === 'Employee' && !!this.tokenState());

  constructor(
    private readonly http: HttpClient,
    private readonly router: Router
  ) {}

  login(request: LoginRequest): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${environment.apiBaseUrl}/auth/login`, request).pipe(
      tap((response) => {
        const decoded = decodeJwt(response.token);
        const role = response.user?.role ?? decoded.role;
        const email = response.user?.email ?? decoded.email ?? request.email;
        this.setSession(response.token, role, email);
      })
    );
  }

  logout(): void {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(ROLE_KEY);
    localStorage.removeItem(EMAIL_KEY);
    this.tokenState.set(null);
    this.roleState.set(null);
    this.emailState.set(null);
    void this.router.navigateByUrl('/login');
  }

  private setSession(token: string, role: Role, email: string): void {
    localStorage.setItem(TOKEN_KEY, token);
    localStorage.setItem(ROLE_KEY, role);
    localStorage.setItem(EMAIL_KEY, email);
    this.tokenState.set(token);
    this.roleState.set(role);
    this.emailState.set(email);
  }
}

function decodeJwt(token: string): { email?: string; role: Role } {
  try {
    const payload = JSON.parse(atob(token.split('.')[1] ?? ''));
    const roleClaim =
      payload.role ??
      payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ??
      'Employee';
    const emailClaim =
      payload.email ??
      payload.sub ??
      payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress'];

    return {
      email: emailClaim,
      role: roleClaim === 'Admin' ? 'Admin' : 'Employee'
    };
  } catch {
    return { role: 'Employee' };
  }
}
