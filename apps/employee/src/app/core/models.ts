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

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  sources: string[];
  pending?: boolean;
}
