import { importProvidersFrom } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FileUp, LucideAngularModule, RefreshCw, Search, UploadCloud } from 'lucide-angular';
import { of } from 'rxjs';
import { DocumentsComponent } from './documents.component';
import { DocumentsService } from '../core/documents.service';

describe('DocumentsComponent', () => {
  let fixture: ComponentFixture<DocumentsComponent>;
  let service: jasmine.SpyObj<DocumentsService>;

  beforeEach(async () => {
    service = jasmine.createSpyObj<DocumentsService>('DocumentsService', ['list', 'upload']);
    service.list.and.returnValue(of([]));
    service.upload.and.returnValue(of({ documentId: 'doc-123' }));

    await TestBed.configureTestingModule({
      imports: [DocumentsComponent],
      providers: [
        { provide: DocumentsService, useValue: service },
        importProvidersFrom(
          LucideAngularModule.pick({ FileUp, RefreshCw, Search, UploadCloud })
        )
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DocumentsComponent);
    fixture.detectChanges();
  });

  it('uploads a selected file and shows the document id', () => {
    const file = new File(['policy'], 'policy.pdf', { type: 'application/pdf' });
    fixture.componentInstance.selectedFile.set(file);

    fixture.componentInstance.upload();

    expect(service.upload).toHaveBeenCalledWith(file);
    expect(fixture.componentInstance.uploadResult()).toContain('doc-123');
  });
});
