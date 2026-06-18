import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, signal } from '@angular/core';
import { LucideAngularModule } from 'lucide-angular';
import { finalize } from 'rxjs';
import { AuditService } from '../core/audit.service';
import { AuditLog } from '../core/models';

@Component({
  selector: 'rag-audit',
  standalone: true,
  imports: [DatePipe, LucideAngularModule],
  templateUrl: './audit.component.html',
  styleUrl: './audit.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AuditComponent implements OnInit {
  readonly logs = signal<AuditLog[]>([]);
  readonly loading = signal(false);
  readonly error = signal('');

  constructor(private readonly auditService: AuditService) {}

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.loading.set(true);
    this.error.set('');
    this.auditService
      .list()
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (logs) => this.logs.set(logs),
        error: () => this.error.set('Could not load audit logs from the API.')
      });
  }
}
