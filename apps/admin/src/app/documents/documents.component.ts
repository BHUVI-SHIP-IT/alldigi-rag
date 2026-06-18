import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { LucideAngularModule } from 'lucide-angular';
import { finalize, interval, Subscription, switchMap } from 'rxjs';
import { DocumentsService } from '../core/documents.service';
import { DocumentRecord } from '../core/models';

@Component({
  selector: 'rag-documents',
  standalone: true,
  imports: [
    DatePipe,
    FormsModule,
    LucideAngularModule
  ],
  templateUrl: './documents.component.html',
  styleUrl: './documents.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DocumentsComponent implements OnInit, OnDestroy {
  readonly documents = signal<DocumentRecord[]>([]);
  readonly loading = signal(false);
  readonly uploading = signal(false);
  readonly error = signal('');
  readonly uploadResult = signal('');
  readonly query = signal('');
  readonly selectedFile = signal<File | null>(null);

  readonly filteredDocuments = computed(() => {
    const term = this.query().trim().toLowerCase();
    if (!term) {
      return this.documents();
    }
    return this.documents().filter((document) => document.fileName.toLowerCase().includes(term));
  });

  readonly indexedCount = computed(() => this.documents().filter((document) => document.status === 'indexed').length);
  readonly processingCount = computed(() =>
    this.documents().filter((document) => document.status === 'queued' || document.status === 'processing').length
  );

  private poll?: Subscription;

  constructor(private readonly documentsService: DocumentsService) {}

  ngOnInit(): void {
    this.refresh();
    this.poll = interval(7000)
      .pipe(switchMap(() => this.documentsService.list()))
      .subscribe({
        next: (documents) => this.documents.set(documents),
        error: () => undefined
      });
  }

  ngOnDestroy(): void {
    this.poll?.unsubscribe();
  }

  chooseFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile.set(input.files?.[0] ?? null);
    this.uploadResult.set('');
  }

  refresh(): void {
    this.loading.set(true);
    this.error.set('');
    this.documentsService
      .list()
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (documents) => this.documents.set(documents),
        error: () => this.error.set('Could not load documents from the API.')
      });
  }

  upload(): void {
    const file = this.selectedFile();
    if (!file || this.uploading()) {
      return;
    }

    this.uploading.set(true);
    this.error.set('');
    this.documentsService
      .upload(file)
      .pipe(finalize(() => this.uploading.set(false)))
      .subscribe({
        next: (response) => {
          this.uploadResult.set(`Queued as ${response.documentId}`);
          this.selectedFile.set(null);
          this.refresh();
        },
        error: () => this.error.set('Upload failed. Confirm the API is running and your token is valid.')
      });
  }

  statusLabel(status: DocumentRecord['status']): string {
    return status[0].toUpperCase() + status.slice(1);
  }
}
