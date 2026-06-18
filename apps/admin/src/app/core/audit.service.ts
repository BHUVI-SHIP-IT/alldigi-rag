import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from './environment';
import { AuditLog } from './models';

@Injectable({ providedIn: 'root' })
export class AuditService {
  constructor(private readonly http: HttpClient) {}

  list(): Observable<AuditLog[]> {
    return this.http.get<AuditLog[]>(`${environment.apiBaseUrl}/audit`);
  }
}
