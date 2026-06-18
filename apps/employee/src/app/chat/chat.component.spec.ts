import { importProvidersFrom } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { BookOpenCheck, LogOut, LucideAngularModule, Send, ShieldCheck, Square } from 'lucide-angular';
import { AuthService } from '../core/auth.service';
import { QueryService } from '../core/query.service';
import { ChatComponent } from './chat.component';

describe('ChatComponent', () => {
  let fixture: ComponentFixture<ChatComponent>;
  let queryService: jasmine.SpyObj<QueryService>;

  beforeEach(async () => {
    queryService = jasmine.createSpyObj<QueryService>('QueryService', ['ask']);
    queryService.ask.and.callFake(async (_question, handlers) => {
      handlers.onToken('Hello ');
      handlers.onToken('there');
      handlers.onSources(['handbook.pdf']);
    });

    const auth = {
      email: () => 'employee@company.local',
      logout: jasmine.createSpy('logout')
    };

    await TestBed.configureTestingModule({
      imports: [ChatComponent],
      providers: [
        { provide: QueryService, useValue: queryService },
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: jasmine.createSpyObj<Router>('Router', ['navigateByUrl']) },
        importProvidersFrom(
          LucideAngularModule.pick({ BookOpenCheck, LogOut, Send, ShieldCheck, Square })
        )
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ChatComponent);
    fixture.detectChanges();
  });

  it('appends streamed tokens and renders sources', async () => {
    fixture.componentInstance.draft.set('What is leave policy?');

    await fixture.componentInstance.send();

    const assistant = fixture.componentInstance.messages().at(-1);
    expect(assistant?.content).toBe('Hello there');
    expect(assistant?.sources).toEqual(['handbook.pdf']);
  });
});
