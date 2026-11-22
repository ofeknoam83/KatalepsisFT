import { ChangeDetectionStrategy, Component } from '@angular/core';
import { NzEmptyModule } from 'ng-zorro-antd/empty';

@Component({
  selector: 'app-home-page',
  imports: [NzEmptyModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 style="margin:0 0 16px">Fund</h2>
    <div style="background:#fff;border:1px solid #f0f0f0;border-radius:8px;padding:48px">
      <nz-empty nzNotFoundContent="No funds yet. Funds, sleeves and managers arrive with the next phase." />
    </div>
  `,
})
export class HomePage {}
