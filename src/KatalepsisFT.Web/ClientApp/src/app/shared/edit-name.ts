import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, ElementRef, inject, input, output, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Store } from '@ngxs/store';
import { LiveService } from '../core/live.service';
import { FundsApi } from '../funds/funds.api';
import { LoadFunds } from '../funds/view.state';

/**
 * A name with a pencil next to it. Clicking the pencil turns the name into a box; Enter or Save sends the new name
 * to `url`, Escape or Cancel leaves it as it was. Without a `url` it is just the name.
 */
@Component({
  selector: 'app-edit-name',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!editing()) {
      <span class="shown">{{ value() }}</span>
      @if (url()) {
        <button type="button" class="pencil" (click)="start($event)" [title]="'Rename ' + what()" [attr.aria-label]="'Rename ' + what()" data-testid="rename">
          <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true"><path d="M11.3 1.9a1.5 1.5 0 0 1 2.1 0l.7.7a1.5 1.5 0 0 1 0 2.1L5.6 13.2l-3.4.8.8-3.4z" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round" /></svg>
        </button>
      }
    } @else {
      <span class="edit" (click)="$event.stopPropagation()">
        <input #box [ngModel]="draft()" (ngModelChange)="draft.set($event)" [maxlength]="maxlength()" [style.width.ch]="width()" (keydown.enter)="save()" (keydown.escape)="cancel()" [disabled]="saving()" data-testid="rename-input" />
        <button type="button" class="go" [disabled]="saving() || draft().trim() === ''" (click)="save()" data-testid="rename-save">Save</button>
        <button type="button" class="no" [disabled]="saving()" (click)="cancel()">Cancel</button>
        @if (error(); as message) {
          <span class="error" data-testid="rename-error">{{ message }}</span>
        }
      </span>
    }
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
      font: inherit;
    }

    .pencil {
      border: 0;
      background: none;
      padding: 3px;
      line-height: 0;
      border-radius: 4px;
      color: #bfbfbf;
      cursor: pointer;
    }

    .pencil:hover,
    .pencil:focus-visible {
      color: #1677ff;
      background: #e6f0ff;
    }

    .edit {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      flex-wrap: wrap;
    }

    input {
      font: inherit;
      min-width: 12ch;
      max-width: 60ch;
      padding: 1px 8px;
      border: 1px solid #1677ff;
      border-radius: 6px;
      outline: none;
      background: #fff;
    }

    .go,
    .no {
      font-size: 13px;
      font-weight: 400;
      border: 0;
      background: none;
      padding: 2px 4px;
      cursor: pointer;
      color: #1677ff;
    }

    .no {
      color: #8c8c8c;
    }

    .go[disabled] {
      color: #bfbfbf;
      cursor: default;
    }

    .error {
      font-size: 12px;
      font-weight: 400;
      color: #cf1322;
    }
  `,
})
export class EditName {
  private readonly http = inject(HttpClient);
  private readonly live = inject(LiveService);
  private readonly store = inject(Store);
  private readonly funds = inject(FundsApi);
  private readonly box = viewChild<ElementRef<HTMLInputElement>>('box');

  readonly value = input.required<string>();
  /** Where the new name is sent, as { name }. Null or empty: the name cannot be changed here. */
  readonly url = input<string | null>(null);
  /** What is being renamed, for the pencil's tooltip: "the fund", "this wallet". */
  readonly what = input('this');
  readonly maxlength = input(200);
  readonly saved = output<string>();

  protected readonly editing = signal(false);
  protected readonly draft = signal('');
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected width(): number {
    return Math.min(60, Math.max(12, this.draft().length + 3));
  }

  protected start(event: Event): void {
    event.stopPropagation();
    this.draft.set(this.value());
    this.error.set(null);
    this.editing.set(true);
    setTimeout(() => {
      this.box()?.nativeElement.focus();
      this.box()?.nativeElement.select();
    });
  }

  protected cancel(): void {
    this.editing.set(false);
  }

  protected save(): void {
    const name = this.draft().trim();
    const url = this.url();
    if (name === '' || !url || this.saving()) {
      return;
    }

    if (name === this.value()) {
      this.editing.set(false);
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.http.post(url, { name }).subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(false);
        this.saved.emit(name);
        // The name shows in the menu, in breadcrumbs and on other panels of the page: load them all again.
        this.store.dispatch(new LoadFunds());
        this.funds.refreshNames();
        this.live.refresh();
      },
      error: (e: { status?: number; error?: { message?: string } }) => {
        this.saving.set(false);
        this.error.set(e.error?.message ?? (e.status === 403 ? 'Only an administrator can rename.' : 'The name could not be saved. Try again.'));
      },
    });
  }
}
