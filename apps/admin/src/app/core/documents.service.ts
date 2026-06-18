import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from './environment';
import { DocumentRecord, UploadResponse } from './models';

@Injectable({ providedIn: 'root' })
export class DocumentsService {
  constructor(private readonly http: HttpClient) {}

  list(): Observable<DocumentRecord[]> {
    return this.http.get<DocumentRecord[]>(`${environment.apiBaseUrl}/documents`);
  }

  upload(file: File): Observable<UploadResponse> {
    const body = new FormData();
    body.append('file', file, file.name);
    return this.http.post<UploadResponse>(`${environment.apiBaseUrl}/documents`, body);
  }
}
