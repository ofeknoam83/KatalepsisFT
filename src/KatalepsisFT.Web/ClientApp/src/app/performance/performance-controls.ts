import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Store } from '@ngxs/store';
import { NzSegmentedModule } from 'ng-zorro-antd/segmented';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { SetBenchmark, SetPeriod, ViewState } from '../funds/view.state';
import { Benchmark, PERIODS, PerformancePeriod } from './models';

/** Period and benchmark. Both are remembered while the user moves between pages. */
@Component({
  selector: 'app-performance-controls',
  imports: [FormsModule, NzSegmentedModule, NzSelectModule, NzTagModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div style="display:flex;align-items:center;gap:12px;margin-bottom:14px;flex-wrap:wrap">
      <span style="color:var(--kft-text-3);font-size:12px">Period</span>
      <nz-segmented [nzOptions]="periods" [ngModel]="period()" (nzValueChange)="setPeriod($event)" data-testid="period" />
      <span style="color:var(--kft-text-3);font-size:12px;margin-left:8px">Benchmark</span>
      <nz-select [ngModel]="current().id" (ngModelChange)="setBenchmark($event)" style="width:200px" data-testid="benchmark">
        @for (benchmark of benchmarks(); track benchmark.id) {
          <nz-option [nzValue]="benchmark.id" [nzLabel]="benchmark.name" />
        }
      </nz-select>
      @if (current().isApproximation) {
        <nz-tag nzColor="gold">Approximation, not the official index</nz-tag>
      }
      <span style="flex:1"></span>
      <span style="color:var(--kft-text-3);font-size:12px">{{ range() }}</span>
    </div>
  `,
})
export class PerformanceControls {
  private readonly store = inject(Store);

  readonly benchmarks = input.required<Benchmark[]>();
  readonly current = input.required<Benchmark>();
  readonly range = input('');

  protected readonly periods = PERIODS;
  protected readonly period = this.store.selectSignal(ViewState.period);

  protected setPeriod(value: string | number): void {
    this.store.dispatch(new SetPeriod(value as PerformancePeriod));
  }

  protected setBenchmark(id: number): void {
    this.store.dispatch(new SetBenchmark(id));
  }
}
