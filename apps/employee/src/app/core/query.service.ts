import { Injectable } from '@angular/core';
import { environment } from './environment';

export interface StreamHandlers {
  onToken: (token: string) => void;
  onSources: (sources: string[]) => void;
}

@Injectable({ providedIn: 'root' })
export class QueryService {
  async ask(question: string, handlers: StreamHandlers, signal?: AbortSignal): Promise<void> {
    const token = localStorage.getItem('rag_employee_token');
    const response = await fetch(`${environment.apiBaseUrl}/query`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {})
      },
      body: JSON.stringify({ question }),
      signal
    });

    if (!response.ok || !response.body) {
      throw new Error('Query request failed');
    }

    await readEventStream(response.body, handlers);
  }
}

async function readEventStream(stream: ReadableStream<Uint8Array>, handlers: StreamHandlers): Promise<void> {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  while (true) {
    const { value, done } = await reader.read();
    if (done) {
      break;
    }

    buffer += decoder.decode(value, { stream: true });
    const events = buffer.split('\n\n');
    buffer = events.pop() ?? '';

    for (const event of events) {
      handleEvent(event, handlers);
    }
  }

  if (buffer.trim()) {
    handleEvent(buffer, handlers);
  }
}

function handleEvent(raw: string, handlers: StreamHandlers): void {
  const lines = raw.split('\n').map((line) => line.trim());
  const eventName = lines.find((line) => line.startsWith('event:'))?.slice(6).trim() ?? 'message';
  const data = lines
    .filter((line) => line.startsWith('data:'))
    .map((line) => line.slice(5).trim())
    .join('\n');

  if (!data || data === '[DONE]') {
    return;
  }

  if (eventName === 'sources') {
    handlers.onSources(JSON.parse(data) as string[]);
    return;
  }

  try {
    const parsed = JSON.parse(data) as { token?: string; content?: string };
    handlers.onToken(parsed.token ?? parsed.content ?? data);
  } catch {
    handlers.onToken(data);
  }
}
