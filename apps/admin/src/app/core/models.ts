export type Role = 'Admin' | 'Employee';

export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  token: string;
  user?: {
    email: string;
    role: Role;
  };
}

export interface DocumentRecord {
  id: string;
  fileName: string;
  uploader?: string;
  status: 'queued' | 'processing' | 'indexed' | 'failed';
  createdAt: string;
  chunkCount?: number;
}

export interface UploadResponse {
  documentId: string;
}

export interface AuditLog {
  id: string;
  userEmail: string;
  query: string;
  retrievedSources: string[];
  createdAt: string;
}
