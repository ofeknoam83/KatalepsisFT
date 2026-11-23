import { DatePipe, DecimalPipe, LowerCasePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzCheckboxModule } from 'ng-zorro-antd/checkbox';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzInputNumberModule } from 'ng-zorro-antd/input-number';
import { NzPopconfirmModule } from 'ng-zorro-antd/popconfirm';
import { NzSegmentedModule } from 'ng-zorro-antd/segmented';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { Observable, combineLatest, filter, map, merge, of, timer } from 'rxjs';
import { FundTabs } from '../funds/fund-tabs';
import { FundsApi } from '../funds/funds.api';
import { DynamicDecimalPipe } from '../shared/dynamic-decimal.pipe';
import { EditName } from '../shared/edit-name';
import { FORMAT_PIPES } from '../shared/format';
import { PageHead } from '../shared/page-head';
import { keptData } from '../shared/page-data';
import { APPROVAL_LABEL, AccountForm, Book, HaltScope, Order, OrderSide, OrderType, STATUS_COLOR, STATUS_LABEL, TIF_LABEL, TimeInForce, TradedType, Trading, TradingAccount, TradingVenue, instrumentName } from './models';

interface Answer {
  notice?: string | null;
}

interface Depth {
  venue: string;
  price: number;
  quantity: number;
  total: number;
}

/**
 * Trading for one fund or separately managed account: its trading accounts and positions, the order ticket, the
 * consolidated book of the four venues, the orders with everything that happened to each, the kill switches, and
 * the requests waiting for a second person.
 */
@Component({
  selector: 'app-fund-trading-page',
  imports: [DatePipe, DecimalPipe, LowerCasePipe, EditName, FormsModule, NzAlertModule, NzButtonModule, NzCheckboxModule, NzInputModule, NzInputNumberModule, NzPopconfirmModule, NzSegmentedModule, NzSelectModule, NzTableModule, NzTagModule, PageHead, FundTabs, DynamicDecimalPipe, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load trading for this fund." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head [crumbs]="[{ label: d.fundName, link: ['/funds', d.fundId] }, { label: 'Trading' }]" [title]="d.fundName" [renameUrl]="'/api/funds/' + d.fundId + '/name'" renameWhat="the fund" [demo]="d.isDemo" sub="Orders, positions and the market across venues" [showFilter]="false" />
      <app-fund-tabs [fundId]="d.fundId" />

      <div class="status" data-testid="engine-status">
        @if (anyLive(d)) {
          <nz-tag nzColor="red" data-testid="live-tag">Real money</nz-tag>
          <span class="hint">Accounts marked Live send real orders to their venue. Paper accounts fill against live books and move no money.</span>
        } @else {
          <nz-tag nzColor="gold">Paper trading</nz-tag>
          <span class="hint">Orders fill against the venues' real, live books. No order is sent to a venue and no money moves.</span>
        }
        <span style="flex:1"></span>
        @if (d.engine; as engine) {
          @for (v of engine.venues; track v.venue) {
            <span class="venue" [class.on]="v.connected && v.books > 0" [title]="v.connected ? v.books + ' live books' : 'No market data from this venue right now'">
              <i></i>{{ venueName(d, v.venue) }}
              @if (v.roundTripMs !== null) {
                <small>{{ v.roundTripMs | number: '1.0-0' }} ms</small>
              }
            </span>
          }
        } @else {
          <span class="venue"><i></i>The execution service is not answering</span>
        }
      </div>
      @if (!d.engine) {
        <nz-alert nzType="warning" nzShowIcon nzMessage="The execution service has not been heard from in the last few seconds." nzDescription="Orders can still be placed: they are written down and wait. Nothing fills until the service is back." class="gap" data-testid="engine-missing" />
      }
      @if (d.isDemo) {
        <nz-alert nzType="info" nzShowIcon nzMessage="The demo fund does not trade." class="gap" />
      }

      @for (halt of d.halts; track halt.id) {
        <nz-alert nzType="error" nzShowIcon class="gap" [nzMessage]="haltTitle" [nzDescription]="haltBody" [attr.data-testid]="'halt-' + halt.id" />
        <ng-template #haltTitle>Trading is halted: {{ halt.where }}</ng-template>
        <ng-template #haltBody>
          {{ halt.reason }} · pulled by {{ halt.pulledBy }} at {{ halt.pulledAt | date: 'd MMM HH:mm:ss' : 'UTC' }} UTC.
          @if (halt.releaseRequested) {
            <b>A release has been asked for and waits for an administrator other than the one who asked.</b>
          } @else if (d.canTrade) {
            <a (click)="run(http.post('/api/halts/' + halt.id + '/release', {}), 'The release has been asked for.')" data-testid="ask-release">Ask to release it</a>
          }
        </ng-template>
      }

      @if (error(); as message) {
        <nz-alert nzType="error" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="error.set(null)" class="gap" data-testid="trading-error" />
      }
      @if (saved(); as message) {
        <nz-alert nzType="success" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="saved.set(null)" class="gap" data-testid="trading-saved" />
      }

      @if (d.approvals.length > 0) {
        <div class="panel" data-testid="approvals">
          <h3>Waiting for a second person <span class="hint">Nobody can approve their own request</span></h3>
          <nz-table [nzData]="d.approvals" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <tbody>
              @for (a of d.approvals; track a.id) {
                <tr>
                  <td class="nowrap"><nz-tag>{{ approvalLabel[a.kind] }}</nz-tag></td>
                  <td>{{ a.summary }}</td>
                  <td class="nowrap">{{ a.requestedBy }} · {{ a.requestedAt | date: 'd MMM HH:mm' : 'UTC' }} UTC</td>
                  <td class="nowrap" style="text-align:right">
                    @if (a.canDecide) {
                      <a nz-popconfirm nzPopconfirmTitle="Approve this request?" nzOkText="Approve" (nzOnConfirm)="run(http.post('/api/approvals/' + a.id + '/approve', {}), 'Approved.')" [attr.data-testid]="'approve-' + a.id">Approve</a>
                      <a style="margin-left:12px;color:#cf1322" (click)="run(http.post('/api/approvals/' + a.id + '/reject', {}), 'Rejected.')">Reject</a>
                    }
                    @if (a.canCancel) {
                      <a style="margin-left:12px" (click)="run(http.post('/api/approvals/' + a.id + '/cancel', {}), 'Request withdrawn.')">Withdraw</a>
                    }
                    @if (!a.canDecide && !a.canCancel) {
                      <span class="hint">Needs an administrator</span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </nz-table>
        </div>
      }

      @if (d.accounts.length > 0) {
        <div class="desk">
          <div class="panel" data-testid="ticket">
            <h3>Order ticket</h3>
            <div class="field">
              <label>Account</label>
              <nz-select [ngModel]="target()" (ngModelChange)="target.set($event)" style="width:100%" data-testid="ticket-account">
                @for (m of bestOf(d); track m.id) {
                  <nz-option [nzValue]="'m' + m.id" [nzLabel]="m.name + ' · best venue'" />
                }
                @for (a of d.accounts; track a.id) {
                  <nz-option [nzValue]="'a' + a.id" [nzLabel]="a.manager + ' · ' + a.venueName + (a.isHalted ? ' (halted)' : '')" />
                }
              </nz-select>
            </div>
            <div class="two">
              <div class="field">
                <label>Instrument</label>
                <nz-select [ngModel]="instrument()" (ngModelChange)="instrument.set($event)" nzShowSearch style="width:100%" data-testid="ticket-asset">
                  @for (i of instruments(d); track i.type + i.asset) {
                    <nz-option [nzValue]="i.type + ':' + i.asset" [nzLabel]="name(i.asset, i.type)" />
                  }
                </nz-select>
              </div>
              <div class="field">
                <label>Type</label>
                <nz-segmented [nzOptions]="['Market', 'Limit']" [ngModel]="type()" (ngModelChange)="type.set($event)" data-testid="ticket-type" />
              </div>
            </div>
            <div class="two">
              <div class="field">
                <label>Quantity, {{ asset() }}</label>
                <nz-input-number [ngModel]="quantity()" (ngModelChange)="quantity.set($event)" [nzMin]="0" [nzStep]="0.01" style="width:100%" data-testid="ticket-quantity" />
              </div>
              <div class="field">
                <label>Limit price</label>
                <nz-input-number [ngModel]="limitPrice()" (ngModelChange)="limitPrice.set($event)" [nzMin]="0" [nzDisabled]="type() === 'Market'" [nzPlaceHolder]="type() === 'Market' ? 'Market' : ''" style="width:100%" data-testid="ticket-limit" />
              </div>
            </div>
            @if (type() === 'Limit') {
              <div class="field">
                <label>How long</label>
                <nz-select [ngModel]="tif()" (ngModelChange)="tif.set($event)" style="width:100%">
                  @for (t of tifs; track t) {
                    <nz-option [nzValue]="t" [nzLabel]="tifLabel[t]" />
                  }
                </nz-select>
              </div>
            }
            @if (type2() === 'Perp') {
              <label nz-checkbox [ngModel]="reduceOnly()" (ngModelChange)="reduceOnly.set($event)">Reduce only <span class="hint">may shrink a position, never open or flip one</span></label>
            } @else {
              <span class="hint">Spot: you can sell only what the account holds.</span>
            }
            <div class="est">
              About <b>{{ notional() | usd }}</b>
              @if (book.data()?.mid; as mid) {
                at {{ mid | dynamicDecimal }}
              }
            </div>
            <div class="two">
              <button nz-button class="buy" [disabled]="!canSend(d)" nz-popconfirm [nzCondition]="!targetIsLive(d)" [nzPopconfirmTitle]="'Send a real order to buy ' + (quantity() ?? 0) + ' ' + asset() + '?'" nzOkText="Send it" (nzOnConfirm)="send('Buy')" data-testid="ticket-buy">Buy {{ asset() }}</button>
              <button nz-button class="sell" [disabled]="!canSend(d)" nz-popconfirm [nzCondition]="!targetIsLive(d)" [nzPopconfirmTitle]="'Send a real order to sell ' + (quantity() ?? 0) + ' ' + asset() + '?'" nzOkText="Send it" (nzOnConfirm)="send('Sell')" data-testid="ticket-sell">Sell {{ asset() }}</button>
            </div>
            @if (targetIsLive(d)) {
              <p class="note" style="color:#cf1322" data-testid="ticket-live">This account trades real money. A market order goes out priced at the edge of the account's price band, to trade now or not at all.</p>
            }
            @if (!d.canTrade) {
              <p class="note">Your account can look but not trade. Trading needs the Trader or Admin role.</p>
            }
          </div>

          <div class="panel" data-testid="book">
            <h3>
              {{ name(asset(), type2()) }} across venues
              @if (book.data(); as b) {
                @if (b.spreadBps !== null) {
                  <span class="hint">best bid {{ b.bestBid ?? 0 | dynamicDecimal }} on {{ venueName(d, b.bestBidVenue) }} · best ask {{ b.bestAsk ?? 0 | dynamicDecimal }} on {{ venueName(d, b.bestAskVenue) }} · {{ b.spreadBps | number: '1.2-2' }} bp apart</span>
                }
              }
            </h3>
            @if (book.data(); as b) {
              @if (b.venues.length === 0) {
                <p class="note" data-testid="book-empty">No venue has a live book for {{ name(asset(), type2()) }} right now. Orders for it are refused until one does.</p>
              } @else {
                <div class="books">
                  <nz-table [nzData]="b.venues" nzTemplateMode [nzShowPagination]="false" nzSize="small">
                    <thead>
                      <tr><th>Venue</th><th class="num">Bid size</th><th class="num">Bid</th><th class="num">Ask</th><th class="num">Ask size</th><th class="num">Age</th></tr>
                    </thead>
                    <tbody>
                      @for (v of b.venues; track v.venue) {
                        <tr>
                          <td>{{ v.venueName }}</td>
                          <td class="num">{{ v.bidSize ?? 0 | dynamicDecimal }}</td>
                          <td class="num" [class.best]="v.venue === b.bestBidVenue" style="color:#389e0d">{{ v.bestBid ?? 0 | dynamicDecimal }}</td>
                          <td class="num" [class.best]="v.venue === b.bestAskVenue" style="color:#cf1322">{{ v.bestAsk ?? 0 | dynamicDecimal }}</td>
                          <td class="num">{{ v.askSize ?? 0 | dynamicDecimal }}</td>
                          <td class="num hint">{{ v.ageMs | number: '1.0-0' }} ms</td>
                        </tr>
                      }
                    </tbody>
                  </nz-table>
                  <div class="ladder" data-testid="ladder">
                    <div>
                      @for (l of depth().bids; track $index) {
                        <div class="lvl bid"><i [style.width.%]="l.total / depth().most * 100"></i><span>{{ l.venue }}</span><span>{{ l.quantity | dynamicDecimal }}</span><b>{{ l.price | dynamicDecimal }}</b></div>
                      }
                    </div>
                    <div>
                      @for (l of depth().asks; track $index) {
                        <div class="lvl ask"><i [style.width.%]="l.total / depth().most * 100"></i><b>{{ l.price | dynamicDecimal }}</b><span>{{ l.quantity | dynamicDecimal }}</span><span>{{ l.venue }}</span></div>
                      }
                    </div>
                  </div>
                </div>
              }
            }
          </div>
        </div>
      }

      @if (d.accounts.length === 0 && !d.isDemo) {
        <div class="panel start" data-testid="start-trading">
          <h3>Start trading on paper</h3>
          <ol>
            <li><b>Add a trading account</b> for one of this fund's managers on a venue. It starts with a paper balance.</li>
            <li><b>Place an order</b> in the order ticket that then appears here, next to the live prices of the four venues.</li>
            <li><b>Watch it fill</b> in the list of orders, and the position appear under the account.</li>
          </ol>
          @if (d.managers.length === 0) {
            <p class="note">This fund has no manager yet. Add one on the Structure tab first.</p>
          } @else if (d.canAdmin) {
            <button nz-button nzType="primary" (click)="startAdd(d)" data-testid="start-add-account">Add a trading account</button>
          } @else {
            <p class="note">Adding an account needs an administrator.</p>
          }
        </div>
      }

      <div class="panel" data-testid="accounts">
        <h3>
          Trading accounts <span class="hint">One per manager and venue</span>
          <span style="flex:1"></span>
          @if (d.canTrade) {
            <button nz-button nzDanger nzSize="small" (click)="halting.set(!halting())" data-testid="kill-switch">Kill switch</button>
          }
          @if (d.canAdmin) {
            <button nz-button nzSize="small" style="margin-left:8px" (click)="startAdd(d)" data-testid="add-account">Add an account</button>
          }
        </h3>

        @if (halting()) {
          <div class="form" data-testid="halt-form">
            <nz-select [ngModel]="haltScope()" (ngModelChange)="haltScope.set($event)" style="width:230px">
              <nz-option nzValue="Account" nzLabel="One account" />
              <nz-option nzValue="Venue" nzLabel="One venue, every fund" />
              <nz-option nzValue="Global" nzLabel="Everything, every fund" />
            </nz-select>
            @if (haltScope() === 'Account') {
              <nz-select [ngModel]="haltAccount()" (ngModelChange)="haltAccount.set($event)" nzPlaceHolder="Account" style="width:260px">
                @for (a of d.accounts; track a.id) {
                  <nz-option [nzValue]="a.id" [nzLabel]="a.manager + ' · ' + a.venueName" />
                }
              </nz-select>
            }
            @if (haltScope() === 'Venue') {
              <nz-select [ngModel]="haltVenue()" (ngModelChange)="haltVenue.set($event)" style="width:160px">
                @for (v of d.venues; track v.venue) {
                  <nz-option [nzValue]="v.venue" [nzLabel]="v.name" />
                }
              </nz-select>
            }
            <input nz-input [ngModel]="haltReason()" (ngModelChange)="haltReason.set($event)" placeholder="Why" maxlength="300" style="flex:1;min-width:200px" data-testid="halt-reason" />
            <button nz-button nzDanger nzType="primary" [disabled]="haltReason().trim() === '' || (haltScope() === 'Account' && haltAccount() === null)" (click)="pull(d.fundId)" data-testid="halt-pull">Halt trading</button>
            <p class="note" style="flex-basis:100%">Open orders are cancelled and new ones refused, at once. One person can halt; releasing takes two.</p>
          </div>
        }

        @if (form(); as f) {
          <div class="form" data-testid="account-form">
            @if (editing() === null) {
              <div class="field"><label>Manager</label>
                <nz-select [ngModel]="f.managerId" (ngModelChange)="patch({ managerId: $event })" style="width:220px" data-testid="account-manager">
                  @for (m of d.managers; track m.id) {
                    <nz-option [nzValue]="m.id" [nzLabel]="m.name + ' (' + m.sleeve + ')'" />
                  }
                </nz-select>
              </div>
              <div class="field"><label>Venue</label>
                <nz-select [ngModel]="f.venue" (ngModelChange)="patch({ venue: $event })" style="width:150px" data-testid="account-venue">
                  @for (v of d.venues; track v.venue) {
                    <nz-option [nzValue]="v.venue" [nzLabel]="v.name" />
                  }
                </nz-select>
              </div>
            }
            <div class="field"><label>Label</label><input nz-input [ngModel]="f.label" (ngModelChange)="patch({ label: $event })" maxlength="100" style="width:150px" /></div>
            <div class="field"><label>Largest order, USD</label><nz-input-number [ngModel]="f.maxOrderNotionalUsd" (ngModelChange)="patch({ maxOrderNotionalUsd: $event })" [nzMin]="1" [nzStep]="10000" style="width:150px" data-testid="account-max-order" /></div>
            <div class="field"><label>Largest position, USD</label><nz-input-number [ngModel]="f.maxPositionNotionalUsd" (ngModelChange)="patch({ maxPositionNotionalUsd: $event })" [nzMin]="1" [nzStep]="10000" style="width:160px" data-testid="account-max-position" /></div>
            <div class="field"><label>Leverage</label><nz-input-number [ngModel]="f.leverage" (ngModelChange)="patch({ leverage: $event })" [nzMin]="1" [nzMax]="20" style="width:90px" /></div>
            <div class="field"><label>Margin</label>
              <nz-select [ngModel]="f.marginMode" (ngModelChange)="patch({ marginMode: $event })" style="width:110px"><nz-option nzValue="Cross" nzLabel="Cross" /><nz-option nzValue="Isolated" nzLabel="Isolated" /></nz-select>
            </div>
            <div class="field" title="A limit price further than this through the market is refused"><label>Price band, bp</label><nz-input-number [ngModel]="f.priceBandBps" (ngModelChange)="patch({ priceBandBps: $event })" [nzMin]="10" [nzMax]="2000" style="width:110px" /></div>
            <div class="field"><label>Paper balance, USD</label><nz-input-number [ngModel]="f.paperBalanceUsd" (ngModelChange)="patch({ paperBalanceUsd: $event })" [nzMin]="0" [nzStep]="100000" style="width:160px" /></div>
            <div class="field" style="align-self:flex-end">
              <button nz-button nzType="primary" [disabled]="f.managerId === null || f.label.trim() === ''" (click)="saveAccount()" data-testid="account-save">{{ editing() === null ? 'Add the account' : 'Save' }}</button>
              <button nz-button nzType="text" (click)="form.set(null)">Cancel</button>
            </div>
            @if (editing() !== null) {
              <p class="note" style="flex-basis:100%">Tighter limits apply at once. Looser limits, more leverage or a wider price band wait for a second administrator.</p>
            }
          </div>
        }

        @if (keyFor(); as account) {
          <div class="form" data-testid="key-form">
            <div class="field"><label>API key for {{ account.manager }} on {{ account.venueName }}</label><input nz-input [ngModel]="apiKey()" (ngModelChange)="apiKey.set($event)" autocomplete="off" style="width:300px" data-testid="key-key" /></div>
            <div class="field"><label>{{ account.venue === 'Hyperliquid' ? 'API wallet private key' : 'Secret' }}</label><input nz-input type="password" [ngModel]="apiSecret()" (ngModelChange)="apiSecret.set($event)" autocomplete="new-password" style="width:300px" data-testid="key-secret" /></div>
            @if (needsPassphrase(d, account.venue)) {
              <div class="field"><label>Passphrase</label><input nz-input type="password" [ngModel]="passphrase()" (ngModelChange)="passphrase.set($event)" autocomplete="new-password" style="width:180px" /></div>
            }
            <div class="field" style="align-self:flex-end">
              <button nz-button nzType="primary" [disabled]="apiKey().trim() === '' || apiSecret().trim() === ''" (click)="sendKey(account)" data-testid="key-send">Check it and ask for approval</button>
              <button nz-button nzType="text" (click)="closeKey()">Cancel</button>
            </div>
            <p class="note" style="flex-basis:100%">
              {{ account.venue === 'Hyperliquid' ? 'Give the account address as the key and an API wallet as the secret: an API wallet can trade and cannot withdraw.' : 'The key must be able to trade and must not be able to withdraw; it is checked with the venue now and refused otherwise.' }}
              It is stored so that only the execution service can read it, and is used only after a second administrator approves. The account stays on paper until "Go live" is asked for and approved too.
            </p>
          </div>
        }
        @if (d.canAdmin && !d.vaultReady) {
          <nz-alert nzType="warning" nzShowIcon nzMessage="Trading keys cannot be stored yet: the execution service has not prepared its vault." class="gap" />
        }

        <nz-table [nzData]="d.accounts" nzTemplateMode [nzShowPagination]="false" nzSize="small">
          <thead>
            <tr><th>Account</th><th>Mode</th><th class="num">Equity</th><th class="num">Largest order</th><th class="num">Largest position</th><th class="num">Leverage</th><th>Trading key</th><th></th></tr>
          </thead>
          <tbody>
            @for (a of d.accounts; track a.id) {
              <tr [attr.data-testid]="'account-' + a.id">
                <td><b>{{ a.manager }}</b> · {{ a.venueName }} <span class="hint"><app-edit-name [value]="a.label" [url]="d.canAdmin ? '/api/trading-accounts/' + a.id + '/label' : null" what="this trading account" [maxlength]="100" (saved)="again()" /> · {{ a.sleeve }}</span>
                  @if (a.isHalted) {
                    <nz-tag nzColor="red" style="margin-left:6px">Halted</nz-tag>
                  }
                </td>
                <td>
                  <nz-tag [nzColor]="a.mode === 'Paper' ? 'gold' : 'red'">{{ a.mode }}</nz-tag>
                  @if (a.mode === 'Live') {
                    <div class="hint" [title]="'What the account holds is read from ' + a.venueName + ' every few seconds'">{{ a.liveReadAt ? 'read ' + (a.liveReadAt | date: 'HH:mm:ss' : 'UTC') : 'not read yet' }}</div>
                  }
                </td>
                <td class="num">{{ a.equityUsd | usd }}</td>
                <td class="num">{{ a.maxOrderNotionalUsd | usd }}</td>
                <td class="num">{{ a.maxPositionNotionalUsd | usd }}</td>
                <td class="num">{{ a.leverage }}× {{ a.marginMode | lowercase }}</td>
                <td>{{ a.hasKey ? '…' + a.keyHint : 'None' }}</td>
                <td class="nowrap" style="text-align:right">
                  @if (d.canAdmin) {
                    <a (click)="startEdit(a)" [attr.data-testid]="'limits-' + a.id">Limits</a>
                    <a style="margin-left:12px" (click)="openKey(a.id)" [attr.data-testid]="'key-' + a.id">{{ a.hasKey ? 'Replace key' : 'Add key' }}</a>
                    @if (a.canGoLive) {
                      <a
                        style="margin-left:12px;color:#cf1322"
                        nz-popconfirm
                        [nzPopconfirmTitle]="'Ask for ' + a.manager + ' on ' + a.venueName + ' to trade real money, with orders up to ' + (a.maxOrderNotionalUsd | usd) + '? A second administrator has to approve.'"
                        nzOkText="Ask for it"
                        (nzOnConfirm)="run(http.post('/api/trading-accounts/' + a.id + '/go-live', {}), 'Asked for.')"
                        [attr.data-testid]="'go-live-' + a.id"
                        >Go live</a
                      >
                    }
                    @if (a.mode === 'Live') {
                      <a style="margin-left:12px" nz-popconfirm nzPopconfirmTitle="Take this account back to paper? What it holds at the venue is not touched." nzOkText="Back to paper" (nzOnConfirm)="run(http.post('/api/trading-accounts/' + a.id + '/paper', {}), 'Back on paper.')" [attr.data-testid]="'paper-' + a.id">Back to paper</a>
                    }
                  }
                </td>
              </tr>
              @if (a.liveReadError) {
                <tr class="pos">
                  <td colspan="8" style="padding-left:28px;color:#cf1322" [attr.data-testid]="'read-error-' + a.id">{{ a.venueName }}'s account could not be read: {{ a.liveReadError }}. What is shown is the last successful reading.</td>
                </tr>
              }
              @for (p of a.positions; track p.asset + p.type) {
                <tr class="pos" [attr.data-testid]="'position-' + a.id + '-' + p.asset">
                  <td style="padding-left:28px">
                    <b [style.color]="p.quantity > 0 ? '#389e0d' : p.quantity < 0 ? '#cf1322' : '#8c8c8c'">{{ p.quantity > 0 ? (p.type === 'Spot' ? 'Holds' : 'Long') : p.quantity < 0 ? 'Short' : 'Flat' }}</b>
                    {{ abs(p.quantity) | dynamicDecimal }} {{ name(p.asset, p.type) }} <span class="hint">entry {{ p.averagePrice | dynamicDecimal }} · mark {{ p.markPrice ?? 0 | dynamicDecimal }}</span>
                    @if (p.liquidationPrice !== null) {
                      <span class="hint" [style.color]="(p.liquidationDistance ?? 1) < 0.1 ? '#cf1322' : null" title="Where the venue would close this position by force, and how far the market is from it"> · liquidation {{ p.liquidationPrice | dynamicDecimal }}{{ p.liquidationDistance === null ? '' : ' (' + (p.liquidationDistance * 100 | number: '1.0-1') + '% away)' }}</span>
                    }
                  </td>
                  <td></td>
                  <td class="num">{{ p.notionalUsd | usd }}</td>
                  <td class="num" colspan="2">open {{ p.unrealisedPnlUsd | usd }} · closed {{ p.realisedPnlUsd | usd }}</td>
                  <td class="num hint" colspan="2">fees {{ p.feesUsd | usd }}</td>
                  <td class="nowrap" style="text-align:right">
                    @if (d.canTrade && p.quantity !== 0) {
                      <a nz-popconfirm [nzPopconfirmTitle]="p.type === 'Spot' ? 'Sell all of it at the market?' : 'Close this position at the market?'" nzOkText="Close" (nzOnConfirm)="close(a.id, p.asset, p.type, p.quantity)">Close</a>
                    }
                  </td>
                </tr>
              }
            }
            @if (d.accounts.length === 0) {
              <tr><td colspan="8" style="text-align:center;color:#8c8c8c;padding:22px">No trading account yet. {{ d.canAdmin ? 'Add one to start paper trading.' : 'An administrator adds them.' }}</td></tr>
            }
          </tbody>
        </nz-table>
      </div>

      <div class="panel" data-testid="orders">
        <h3>
          Orders
          <span style="flex:1"></span>
          <nz-segmented [nzOptions]="['Open', 'All']" [ngModel]="openOnly() ? 'Open' : 'All'" (ngModelChange)="openOnly.set($event === 'Open')" nzSize="small" />
        </h3>
        <nz-table [nzData]="orders.data() ?? []" nzTemplateMode [nzShowPagination]="false" nzSize="small">
          <thead>
            <tr><th>Time, UTC</th><th>Account</th><th>Side</th><th>Order</th><th class="num">Filled</th><th class="num">Average</th><th class="num">Against arrival</th><th class="num">Fees</th><th>Status</th><th></th></tr>
          </thead>
          <tbody>
            @for (o of orders.data() ?? []; track o.id) {
              <tr (click)="shown.set(shown() === o.id ? null : o.id)" style="cursor:pointer" [attr.data-testid]="'order-' + o.id">
                <td class="nowrap">{{ o.createdAt | date: 'd MMM HH:mm:ss' : 'UTC' }}</td>
                <td>{{ o.account }}</td>
                <td><b [style.color]="o.side === 'Buy' ? '#389e0d' : '#cf1322'">{{ o.side }}</b></td>
                <td class="nowrap">{{ o.quantity | dynamicDecimal }} {{ name(o.asset, o.instrumentType) }} {{ o.type === 'Limit' ? 'at ' + (o.limitPrice ?? 0 | dynamicDecimal) : 'at market' }}
                  @if (o.reduceOnly) {
                    <span class="hint">reduce only</span>
                  }
                </td>
                <td class="num">{{ o.filledQuantity | dynamicDecimal }}</td>
                <td class="num">{{ o.filledQuantity > 0 ? (o.averageFillPrice | dynamicDecimal) : '–' }}</td>
                <td class="num" [title]="'Against the mid price when the order arrived: ' + (o.arrivalPrice ?? 0)">{{ o.slippageBps === null ? '–' : (o.slippageBps | number: '1.1-1') + ' bp' }}</td>
                <td class="num">{{ o.feesUsd | usd }}</td>
                <td><nz-tag [nzColor]="statusColor[o.status]">{{ statusLabel[o.status] }}</nz-tag></td>
                <td class="nowrap" style="text-align:right">
                  @if (d.canTrade && isOpen(o) && !o.cancelRequested) {
                    <a (click)="$event.stopPropagation(); run(http.post('/api/orders/' + o.id + '/cancel', {}), 'Cancel asked for.')" [attr.data-testid]="'cancel-' + o.id">Cancel</a>
                  }
                </td>
              </tr>
              @if (shown() === o.id) {
                <tr class="pos">
                  <td colspan="10" style="padding-left:28px" [attr.data-testid]="'events-' + o.id">
                    @if (o.rejectReason) {
                      <div style="color:#cf1322;margin-bottom:6px">{{ o.rejectReason }}</div>
                    }
                    <div class="hint" style="margin-bottom:4px">{{ tifLabel[o.timeInForce] }} · sent by {{ o.submittedBy }} · name {{ o.clientOrderId }} · acted on in {{ reaction(o) }}</div>
                    @for (e of o.events; track $index) {
                      <div class="event">
                        <span>{{ e.at | date: 'HH:mm:ss.SSS' : 'UTC' }}</span><b>{{ e.kind }}</b>
                        @if (e.kind === 'Fill') {
                          <span>{{ e.quantity ?? 0 | dynamicDecimal }} at {{ e.price ?? 0 | dynamicDecimal }} on {{ venueName(d, e.venue) }} · fee {{ e.feeUsd | usd }}</span>
                        }
                        <span class="hint">{{ e.detail }}</span>
                      </div>
                    }
                  </td>
                </tr>
              }
            }
            @if ((orders.data() ?? []).length === 0) {
              <tr><td colspan="10" style="text-align:center;color:#8c8c8c;padding:22px">{{ openOnly() ? 'No open orders.' : 'No orders yet.' }}</td></tr>
            }
          </tbody>
        </nz-table>
      </div>
    }
  `,
  styles: `
    .gap {
      display: block;
      margin-bottom: 10px;
    }

    .status {
      display: flex;
      align-items: center;
      gap: 14px;
      flex-wrap: wrap;
      margin-bottom: 10px;
    }

    .venue {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
      color: #8c8c8c;
    }

    .venue i {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      background: #ff7875;
    }

    .venue.on {
      color: #262626;
    }

    .venue.on i {
      background: #52c41a;
    }

    .venue small {
      color: #8c8c8c;
    }

    h3 {
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
    }

    .desk {
      display: grid;
      grid-template-columns: 340px 1fr;
      gap: 10px;
      align-items: start;
    }

    @media (max-width: 1100px) {
      .desk {
        grid-template-columns: 1fr;
      }
    }

    .field {
      margin-bottom: 10px;
    }

    .field label {
      display: block;
      font-size: 12px;
      color: #8c8c8c;
      margin-bottom: 3px;
    }

    .two {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 10px;
    }

    .est {
      margin: 10px 0;
      color: #595959;
    }

    .buy,
    .buy:hover {
      background: #389e0d;
      border-color: #389e0d;
      color: #fff;
    }

    .sell,
    .sell:hover {
      background: #cf1322;
      border-color: #cf1322;
      color: #fff;
    }

    .buy[disabled],
    .sell[disabled] {
      opacity: 0.45;
    }

    .start ol {
      margin: 0 0 14px;
      padding-left: 20px;
      color: #595959;
      line-height: 1.9;
    }

    .note {
      color: #8c8c8c;
      font-size: 12px;
      margin: 8px 0 0;
    }

    .form {
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      align-items: flex-start;
      background: #fafafa;
      border: 1px solid #f0f0f0;
      border-radius: 8px;
      padding: 12px;
      margin-bottom: 12px;
    }

    .form .field {
      margin-bottom: 0;
    }

    .pos td {
      background: #fafafa;
    }

    .best {
      font-weight: 600;
    }

    .books {
      display: grid;
      gap: 12px;
    }

    .ladder {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 6px;
      font-size: 12px;
      font-variant-numeric: tabular-nums;
    }

    .lvl {
      position: relative;
      display: grid;
      grid-template-columns: 1fr 1fr 1fr;
      padding: 2px 8px;
    }

    .lvl i {
      position: absolute;
      top: 0;
      bottom: 0;
      opacity: 0.14;
    }

    .lvl.bid {
      text-align: right;
    }

    .lvl.bid span:first-of-type {
      text-align: left;
      color: #8c8c8c;
    }

    .lvl.bid i {
      right: 0;
      background: #389e0d;
    }

    .lvl.bid b {
      color: #389e0d;
    }

    .lvl.ask span:last-of-type {
      text-align: right;
      color: #8c8c8c;
    }

    .lvl.ask span:first-of-type {
      text-align: right;
    }

    .lvl.ask i {
      left: 0;
      background: #cf1322;
    }

    .lvl.ask b {
      color: #cf1322;
    }

    .event {
      display: flex;
      gap: 12px;
      font-size: 12px;
      font-variant-numeric: tabular-nums;
      padding: 1px 0;
    }
  `,
})
export class FundTradingPage {
  protected readonly http = inject(HttpClient);
  private readonly funds = inject(FundsApi);
  protected readonly approvalLabel = APPROVAL_LABEL;
  protected readonly statusLabel = STATUS_LABEL;
  protected readonly statusColor = STATUS_COLOR;
  protected readonly tifLabel = TIF_LABEL;
  protected readonly tifs: TimeInForce[] = ['GoodTillCancelled', 'ImmediateOrCancel', 'PostOnly'];

  protected readonly error = signal<string | null>(null);
  protected readonly saved = signal<string | null>(null);
  private readonly version = signal(0);

  /** Orders, positions and prices move without anyone touching this fund's books, so this page asks every second while it is in view. */
  private readonly beat: Observable<unknown> = merge(timer(0, 1000).pipe(filter(() => !document.hidden)), toObservable(this.version));
  private readonly fundId$ = inject(ActivatedRoute).paramMap.pipe(map((p) => Number(p.get('fundId'))));

  protected readonly page = keptData(this.fundId$, this.beat, (id) => this.http.get<Trading>(`/api/funds/${id}/trading`), (id) => id);
  protected readonly openOnly = signal(false);
  protected readonly orders = keptData(
    combineLatest([this.fundId$, toObservable(this.openOnly)]),
    this.beat,
    ([id, open]) => this.http.get<Order[]>(`/api/funds/${id}/orders?open=${open}`),
    ([id]) => id,
  );
  protected readonly shown = signal<number | null>(null);

  // The ticket.
  protected readonly target = signal<string | null>(null);
  /** The instrument on the ticket, as "Perp:BTC" or "Spot:BTC". */
  protected readonly instrument = signal('Perp:BTC');
  protected readonly asset = computed(() => this.instrument().split(':')[1]);
  protected readonly type2 = computed(() => this.instrument().split(':')[0] as TradedType);
  protected readonly name = instrumentName;
  protected readonly type = signal<OrderType>('Market');
  protected readonly tif = signal<TimeInForce>('GoodTillCancelled');
  protected readonly quantity = signal<number | null>(null);
  protected readonly limitPrice = signal<number | null>(null);
  protected readonly reduceOnly = signal(false);
  private readonly sending = signal(false);

  protected readonly book = keptData(toObservable(this.instrument), this.beat, (i) => this.http.get<Book>(`/api/book/${i.split(':')[0]}/${encodeURIComponent(i.split(':')[1])}`), (i) => i);
  protected readonly notional = computed(() => (this.quantity() ?? 0) * ((this.type() === 'Limit' ? this.limitPrice() : null) ?? this.book.data()?.mid ?? 0));

  /** Every venue's levels in one ladder, best first, with a running total for the bars. */
  protected readonly depth = computed(() => {
    const venues = this.book.data()?.venues ?? [];
    const side = (pick: 'bids' | 'asks'): Depth[] => {
      let total = 0;
      return venues
        .flatMap((v) => v[pick].map((l) => ({ venue: v.venueName, price: l.price, quantity: l.quantity })))
        .sort((a, b) => (pick === 'bids' ? b.price - a.price : a.price - b.price))
        .slice(0, 10)
        .map((l) => ({ ...l, total: (total += l.quantity) }));
    };
    const bids = side('bids');
    const asks = side('asks');
    return { bids, asks, most: Math.max(bids.at(-1)?.total ?? 0, asks.at(-1)?.total ?? 0, 1e-12) };
  });

  // The kill switch.
  protected readonly halting = signal(false);
  protected readonly haltScope = signal<HaltScope>('Account');
  protected readonly haltAccount = signal<number | null>(null);
  protected readonly haltVenue = signal<TradingVenue>('Binance');
  protected readonly haltReason = signal('');

  // Adding an account, or changing one's limits.
  protected readonly form = signal<AccountForm | null>(null);
  protected readonly editing = signal<number | null>(null);

  // A trading key.
  private readonly keyAccount = signal<number | null>(null);
  protected readonly keyFor = computed(() => this.page.data()?.accounts.find((a) => a.id === this.keyAccount()) ?? null);
  protected readonly apiKey = signal('');
  protected readonly apiSecret = signal('');
  protected readonly passphrase = signal('');

  constructor() {
    effect(() => {
      const d = this.page.data();
      if (d === null) {
        return;
      }

      // The menu on the left follows the fund this page is about.
      this.funds.open(d.fundId);
      const chosen = this.target();
      const exists = chosen !== null && (chosen[0] === 'a' ? d.accounts.some((a) => 'a' + a.id === chosen) : d.managers.some((m) => 'm' + m.id === chosen));
      if (!exists && d.accounts.length > 0) {
        this.target.set('a' + d.accounts[0].id);
      }
    });
  }

  /** Loads the page again now, without waiting for the next second. */
  protected again(): void {
    this.version.update((v) => v + 1);
  }

  protected anyLive(d: Trading): boolean {
    return d.accounts.some((a) => a.mode === 'Live');
  }

  /** Whether the ticket's order would be a real one. With "best venue", any real account of the manager makes it so. */
  protected targetIsLive(d: Trading): boolean {
    const target = this.target();
    if (target === null) {
      return false;
    }

    return target[0] === 'a' ? d.accounts.some((a) => 'a' + a.id === target && a.mode === 'Live') : d.accounts.some((a) => 'm' + a.managerId === target && a.mode === 'Live');
  }

  protected abs(value: number): number {
    return Math.abs(value);
  }

  protected venueName(d: Trading, venue: string | null): string {
    return d.venues.find((v) => v.venue === venue)?.name ?? venue ?? '';
  }

  protected needsPassphrase(d: Trading, venue: TradingVenue): boolean {
    return d.venues.find((v) => v.venue === venue)?.needsPassphrase ?? false;
  }

  /**
   * What the chosen account can trade right now: the instruments its venue has a live book for (any venue's, when
   * the system picks the venue). The chosen one stays listed even when its book has gone, so the choice does not jump.
   */
  protected instruments(d: Trading): { asset: string; type: TradedType }[] {
    const target = this.target();
    const venue = target?.[0] === 'a' ? d.accounts.find((a) => 'a' + a.id === target)?.venue : undefined;
    const live = d.instruments.filter((i) => venue === undefined || i.venues.includes(venue));
    const chosen = { asset: this.asset(), type: this.type2() };
    return live.some((i) => i.asset === chosen.asset && i.type === chosen.type) ? live : [chosen, ...live];
  }

  /** Managers with accounts on more than one venue: for them the system can pick the venue with the best price. */
  protected bestOf(d: Trading): { id: number; name: string }[] {
    const count = new Map<number, number>();
    d.accounts.forEach((a) => count.set(a.managerId, (count.get(a.managerId) ?? 0) + 1));
    return d.managers.filter((m) => (count.get(m.id) ?? 0) > 1);
  }

  protected canSend(d: Trading): boolean {
    return d.canTrade && !this.sending() && this.target() !== null && (this.quantity() ?? 0) > 0 && (this.type() === 'Market' || (this.limitPrice() ?? 0) > 0);
  }

  protected isOpen(o: Order): boolean {
    return o.status === 'Received' || o.status === 'Accepted' || o.status === 'Working' || o.status === 'PartlyFilled';
  }

  /** How long the order waited between being written down and the engine first acting on it. */
  protected reaction(o: Order): string {
    if (o.events.length < 2) {
      return 'not yet';
    }

    return `${Math.max(0, new Date(o.events[1].at).getTime() - new Date(o.events[0].at).getTime())} ms`;
  }

  protected send(side: OrderSide): void {
    const target = this.target();
    if (target === null) {
      return;
    }

    this.place({
      tradingAccountId: target[0] === 'a' ? Number(target.slice(1)) : null,
      managerId: target[0] === 'm' ? Number(target.slice(1)) : null,
      asset: this.asset(),
      instrumentType: this.type2(),
      side,
      type: this.type(),
      timeInForce: this.type() === 'Market' ? 'ImmediateOrCancel' : this.tif(),
      quantity: this.quantity(),
      limitPrice: this.type() === 'Limit' ? this.limitPrice() : null,
      reduceOnly: this.type2() === 'Perp' && this.reduceOnly(),
    });
  }

  protected close(accountId: number, asset: string, type: string, quantity: number): void {
    this.place({ tradingAccountId: accountId, managerId: null, asset, instrumentType: type, side: quantity > 0 ? 'Sell' : 'Buy', type: 'Market', timeInForce: 'ImmediateOrCancel', quantity: Math.abs(quantity), limitPrice: null, reduceOnly: type === 'Perp' });
  }

  private place(order: Record<string, unknown>): void {
    this.sending.set(true);
    // The name makes a repeated send (a double click, a retry after a lost answer) the same order, not a second one.
    const body = { ...order, clientOrderId: `ui-${crypto.randomUUID()}` };
    this.run(this.http.post<Answer>('/api/orders', body), `${order['side']} ${order['quantity']} ${order['asset']} sent.`, () => this.sending.set(false));
  }

  protected pull(fundId: number): void {
    const scope = this.haltScope();
    const body = { scope, venue: scope === 'Venue' ? this.haltVenue() : null, tradingAccountId: scope === 'Account' ? this.haltAccount() : null, reason: this.haltReason() };
    this.run(this.http.post<Answer>(`/api/funds/${fundId}/halts`, body), 'Trading halted.', () => undefined, () => {
      this.halting.set(false);
      this.haltReason.set('');
    });
  }

  protected startAdd(d: Trading): void {
    this.editing.set(null);
    this.keyAccount.set(null);
    this.form.set({ managerId: d.managers[0]?.id ?? null, venue: 'Binance', label: 'Main', leverage: 3, marginMode: 'Cross', maxOrderNotionalUsd: 250_000, maxPositionNotionalUsd: 1_000_000, priceBandBps: 100, paperBalanceUsd: 1_000_000 });
  }

  protected startEdit(a: TradingAccount): void {
    this.editing.set(a.id);
    this.keyAccount.set(null);
    this.form.set({ managerId: a.managerId, venue: a.venue, label: a.label, leverage: a.leverage, marginMode: a.marginMode, maxOrderNotionalUsd: a.maxOrderNotionalUsd, maxPositionNotionalUsd: a.maxPositionNotionalUsd, priceBandBps: a.priceBandBps, paperBalanceUsd: a.paperBalanceUsd });
  }

  protected patch(change: Partial<AccountForm>): void {
    this.form.update((f) => (f === null ? f : { ...f, ...change }));
  }

  protected saveAccount(): void {
    const id = this.editing();
    const request = id === null ? this.http.post<Answer>('/api/trading-accounts', this.form()) : this.http.put<Answer>(`/api/trading-accounts/${id}`, this.form());
    this.run(request, id === null ? 'Account added. It trades on paper.' : 'Saved.', () => undefined, () => this.form.set(null));
  }

  protected openKey(accountId: number): void {
    this.form.set(null);
    this.apiKey.set('');
    this.apiSecret.set('');
    this.passphrase.set('');
    this.keyAccount.set(accountId);
  }

  protected closeKey(): void {
    this.openKey(0);
    this.keyAccount.set(null);
  }

  protected sendKey(account: TradingAccount): void {
    const body = { apiKey: this.apiKey().trim(), apiSecret: this.apiSecret().trim(), passphrase: this.passphrase().trim() || null };
    this.run(this.http.post<Answer>(`/api/trading-accounts/${account.id}/key`, body), 'The key passed its check and waits for a second administrator.', () => undefined, () => this.closeKey());
  }

  /** Sends a change, shows what the server said about it, and loads the page again. */
  protected run(request: Observable<unknown>, message: string, always?: () => void, done?: () => void): void {
    this.error.set(null);
    this.saved.set(null);
    request.subscribe({
      next: (answer) => {
        always?.();
        done?.();
        this.saved.set((answer as Answer | null)?.notice ?? message);
        this.version.update((v) => v + 1);
      },
      error: (e: { status?: number; error?: { message?: string } }) => {
        always?.();
        this.error.set(e.error?.message ?? (e.status === 403 ? 'Your account is not allowed to do that.' : 'That did not work. Try again.'));
        this.version.update((v) => v + 1);
      },
    });
  }
}
