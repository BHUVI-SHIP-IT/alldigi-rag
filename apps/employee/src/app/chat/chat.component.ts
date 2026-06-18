import { ChangeDetectionStrategy, Component, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { LucideAngularModule } from 'lucide-angular';
import { AuthService } from '../core/auth.service';
import { ChatMessage } from '../core/models';
import { QueryService } from '../core/query.service';

@Component({
  selector: 'rag-chat',
  standalone: true,
  imports: [FormsModule, LucideAngularModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ChatComponent {
  readonly draft = signal('');
  readonly error = signal('');
  readonly busy = signal(false);
  readonly messages = signal<ChatMessage[]>([
    {
      id: crypto.randomUUID(),
      role: 'assistant',
      content: 'Ask a question about indexed company documents. I will answer with source documents when retrieval completes.',
      sources: []
    }
  ]);

  readonly canSend = computed(() => this.draft().trim().length > 0 && !this.busy());

  private activeAbort?: AbortController;

  constructor(
    readonly auth: AuthService,
    private readonly queryService: QueryService
  ) {}

  async send(): Promise<void> {
    const question = this.draft().trim();
    if (!question || this.busy()) {
      return;
    }

    const userMessage: ChatMessage = {
      id: crypto.randomUUID(),
      role: 'user',
      content: question,
      sources: []
    };
    const assistantMessage: ChatMessage = {
      id: crypto.randomUUID(),
      role: 'assistant',
      content: '',
      sources: [],
      pending: true
    };

    this.messages.update((messages) => [...messages, userMessage, assistantMessage]);
    this.draft.set('');
    this.error.set('');
    this.busy.set(true);
    this.activeAbort = new AbortController();

    try {
      await this.queryService.ask(
        question,
        {
          onToken: (token) => this.appendToken(assistantMessage.id, token),
          onSources: (sources) => this.setSources(assistantMessage.id, sources)
        },
        this.activeAbort.signal
      );
      this.finishMessage(assistantMessage.id);
    } catch {
      this.error.set('The query could not be completed. Confirm the API and model services are healthy.');
      this.finishMessage(assistantMessage.id);
    } finally {
      this.busy.set(false);
      this.activeAbort = undefined;
    }
  }

  stop(): void {
    this.activeAbort?.abort();
    this.busy.set(false);
  }

  private appendToken(id: string, token: string): void {
    this.messages.update((messages) =>
      messages.map((message) => (message.id === id ? { ...message, content: message.content + token } : message))
    );
  }

  private setSources(id: string, sources: string[]): void {
    this.messages.update((messages) =>
      messages.map((message) => (message.id === id ? { ...message, sources } : message))
    );
  }

  private finishMessage(id: string): void {
    this.messages.update((messages) =>
      messages.map((message) =>
        message.id === id
          ? {
              ...message,
              pending: false,
              content: message.content || 'No answer text was returned.'
            }
          : message
      )
    );
  }
}
