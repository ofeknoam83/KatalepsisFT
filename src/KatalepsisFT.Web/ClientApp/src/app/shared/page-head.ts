import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzBreadCrumbModule } from 'ng-zorro-antd/breadcrumb';
import { NzSegmentedModule } from 'ng-zorro-antd/segmented';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { InstrumentFilter } from '../funds/models';
import { SetInstrumentFilter, ViewState } from '../funds/view.state';
import { SessionState } from '../core/session.state';
import { EditName } from './edit-name';

export interface Crumb {
  label: string;
  link?: unknown[];
}

/** Breadcrumb, title, the day the numbers are from, and the instrument filter that follows the user across pages. */
@Component({
  selector: 'app-page-head',
  imports: [DatePipe, FormsModule, RouterLink, EditName, NzBreadCrumbModule, NzSegmentedModule, NzTagModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nz-breadcrumb style="margin-bottom:8px">
      @for (crumb of crumbs(); track crumb.label) {
        <nz-breadcrumb-item>
          @if (crumb.link) {
            <a [routerLink]="crumb.link">{{ crumb.label }}</a>
          } @else {
            {{ crumb.label }}
          }
        </nz-breadcrumb-item>
      }
    </nz-breadcrumb>
    <div class="page-head">
      <h1><app-edit-name [value]="title()" [url]="isAdmin() && !demo() ? renameUrl() : null" [what]="renameWhat()" /></h1>
      @if (demo()) {
        <nz-tag nzColor="gold">Demo fund</nz-tag>
      }
      <span class="sub">{{ sub() }}</span>
      <span class="grow"></span>
      @if (asOf()) {
        <!-- Today's figures are the latest reading and still moving; an earlier day's are that day's last. -->
        <span class="sub">{{ isToday() ? 'Live · today,' : 'As of end of' }} {{ asOf() | date: 'd MMM y' : 'UTC' }} (UTC)</span>
      }
      @if (showFilter()) {
        <nz-segmented [nzOptions]="options" [ngModel]="filter()" (nzValueChange)="setFilter($event)" data-testid="instrument-filter" />
      }
    </div>
  `,
})
export class PageHead {
  private readonly store = inject(Store);

  readonly crumbs = input<Crumb[]>([]);
  readonly title = input.required<string>();
  readonly sub = input('');
  readonly asOf = input<string | null>(null);
  readonly demo = input(false);
  readonly showFilter = input(true);
  /** Where an administrator's new name for the thing in the title is sent; left out, the title cannot be changed here. */
  readonly renameUrl = input<string | null>(null);
  readonly renameWhat = input('this');
  protected readonly isAdmin = this.store.selectSignal(SessionState.isAdmin);

  protected isToday(): boolean {
    return this.asOf() === new Date().toISOString().slice(0, 10);
  }

  protected readonly options: InstrumentFilter[] =['All', 'Spot', 'Perp', 'Option', 'TradFi'];
  protected readonly filter = this.store.selectSignal(ViewState.filter);

  protected setFilter(value: string | number): void {
    this.store.dispatch(new SetInstrumentFilter(value as InstrumentFilter));
  }
}
