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
import { APPROVAL_LABEL, AccountForm, Book, Held, Room, RoomSide, InstrumentRef, VenueRules, WORKING_STATE_COLOR, WORKING_STATE_LABEL, WorkingOrder, WorkingStyle, HaltScope, Order, OrderSide, OrderType, STATUS_COLOR, STATUS_LABEL, TIF_LABEL, TimeInForce, TradedType, Trading, TradingAccount, TradingVenue, instrumentName } from './models';

interface Answer {
  notice?: string | null;
}

/** How many decimals a number is written with, to ten at most. */
function decimals(value: number): number {
  const text = value.toFixed(10).replace(/0+$/, '');
  return text.length - text.indexOf('.') - 1;
}

interface Depth {
  venue: string;
  price: number;
  quantity: number;
  total: number;
}

/**
 * Trading for one fund or separately managed account: its trading accounts and positions, the order ticket, the
 * consolidated book of the venues, the orders with everything that happened to each, the kill switches, and
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
          @if (engine.openInstruments !== null) {
            <span class="venue open" data-testid="open-books" [title]="'Books are opened when an instrument is looked at and closed a few minutes after. Up to ' + (engine.mostOnDemand ?? 0) + ' can be open on demand, on top of the main assets, pins, open orders and positions.'">{{ engine.openInstruments }} instruments open</span>
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
          <h3>
            {{ waiting(d).length > 0 ? 'Waiting for approval' : 'Requests' }}
            <span class="hint">{{ waiting(d).length > 0 ? 'Approved by a second administrator, or by one who is allowed to approve alone' : 'Nothing is waiting. The latest decisions are below.' }}</span>
          </h3>
          <nz-table [nzData]="d.approvals" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <tbody>
              @for (a of waiting(d); track a.id) {
                <tr [attr.data-testid]="'request-' + a.id">
                  <td class="nowrap"><nz-tag>{{ approvalLabel[a.kind] }}</nz-tag></td>
                  <td>{{ a.summary }}</td>
                  <td class="nowrap">{{ a.requestedBy }} · {{ a.requestedAt | date: 'd MMM HH:mm' : 'UTC' }} UTC</td>
                  <td class="nowrap" style="text-align:right">
                    @if (a.canDecide) {
                      <a nz-popconfirm [nzPopconfirmTitle]="a.canCancel ? 'Approve your own request? It is recorded that you approved it alone.' : 'Approve this request?'" nzOkText="Approve" (nzOnConfirm)="run(http.post('/api/approvals/' + a.id + '/approve', {}), 'Approved.')" [attr.data-testid]="'approve-' + a.id">Approve</a>
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
              @for (a of decided(d); track a.id) {
                <tr class="decided" [attr.data-testid]="'decided-' + a.id">
                  <td class="nowrap"><nz-tag>{{ approvalLabel[a.kind] }}</nz-tag></td>
                  <td>{{ a.summary }}</td>
                  <td class="nowrap">{{ a.requestedBy }} · {{ a.requestedAt | date: 'd MMM HH:mm' : 'UTC' }} UTC</td>
                  <td class="nowrap" style="text-align:right" [title]="a.note ?? ''">
                    <nz-tag [nzColor]="a.status === 'Approved' ? 'green' : a.status === 'Rejected' ? 'red' : 'default'">{{ a.status === 'Cancelled' ? 'Withdrawn' : a.status }}</nz-tag>
                    <span class="hint">{{ a.status === 'Cancelled' ? '' : 'by ' + (a.decidedBy ?? '') }}{{ a.decidedAt ? ' · ' + (a.decidedAt | date: 'd MMM HH:mm' : 'UTC') + ' UTC' : '' }}</span>
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
            <div class="quick" data-testid="quick-instruments">
              @for (i of d.pinned; track i.type + i.asset) {
                <span class="pick pin" [class.on]="instrument() === i.type + ':' + i.asset" (click)="instrument.set(i.type + ':' + i.asset)" [title]="'Pinned: its book is always open'">{{ name(i.asset, i.type) }}</span>
              }
              @for (i of unpinnedRecent(d); track i.type + i.asset) {
                <span class="pick" [class.on]="instrument() === i.type + ':' + i.asset" (click)="instrument.set(i.type + ':' + i.asset)" title="Traded lately">{{ name(i.asset, i.type) }}</span>
              }
              @if (d.canTrade) {
                @if (isPinned(d)) {
                  <a class="pinner" (click)="unpin(d)" data-testid="unpin">Unpin {{ name(asset(), type2()) }}</a>
                } @else {
                  <a class="pinner" (click)="pin(d)" data-testid="pin">Pin {{ name(asset(), type2()) }}</a>
                }
              }
            </div>
            <div class="field">
              <label>Account</label>
              <nz-select [ngModel]="target()" (ngModelChange)="target.set($event)" style="width:100%" data-testid="ticket-account">
                @for (m of bestOf(d); track m.id) {
                  <nz-option [nzValue]="'m' + m.id" [nzLabel]="m.name + ' · best venue'" />
                }
                @for (a of d.accounts; track a.id) {
                  <nz-option [nzValue]="'a' + a.id" [nzLabel]="a.manager + ' · ' + a.venueName + (a.perpQuote === 'USDT' ? '' : ' · ' + a.perpQuote + ' perps') + (a.isHalted ? ' (halted)' : '')" />
                }
              </nz-select>
            </div>
            <div class="two">
              <div class="field">
                <label>Instrument</label>
                <nz-select [ngModel]="instrument()" (ngModelChange)="instrument.set($event)" nzShowSearch (nzOnSearch)="search($event)" nzPlaceHolder="Type to search every venue" style="width:100%" data-testid="ticket-asset">
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
                <nz-input-number [ngModel]="quantity()" (ngModelChange)="quantity.set($event)" [nzMin]="0" [nzStep]="room.data()?.quantityStep ?? 0.01" style="width:100%" data-testid="ticket-quantity" />
              </div>
              <div class="field">
                <label>Limit price</label>
                <nz-input-number [ngModel]="limitPrice()" (ngModelChange)="limitPrice.set($event)" [nzMin]="0" [nzDisabled]="type() === 'Market'" [nzPlaceHolder]="type() === 'Market' ? 'Market' : ''" style="width:100%" data-testid="ticket-limit" />
              </div>
            </div>
            @if (room.data(); as r) {
              <div class="room" data-testid="room">
                @if (r.held.length === 0) {
                  <div class="hint" data-testid="position-line">No position in {{ name(asset(), type2()) }} on {{ r.buy?.venueName ?? r.sell?.venueName ?? 'this account' }}.</div>
                }
                @for (h of r.held; track h.tradingAccountId) {
                  <div class="held" data-testid="position-line">
                    <div class="line">
                      <span class="side" [class.short]="h.quantity < 0">{{ type2() === 'Spot' ? 'Holds' : h.quantity > 0 ? 'Long' : 'Short' }}</span>
                      <b>{{ abs(h.quantity) | dynamicDecimal }} {{ asset() }}</b>
                      @if (h.markPrice !== null) { <span class="worth">{{ abs(h.quantity) * h.markPrice | usd }}</span> }
                    </div>
                    <div class="line sub">
                      <span>@if (r.held.length > 1 || target()?.[0] === 'm') { {{ h.venueName }} · }Entry {{ h.averagePrice | dynamicDecimal }}</span>
                      @if (h.unrealisedPnlUsd !== null) { <span class="worth">Open P&amp;L <b [style.color]="h.unrealisedPnlUsd >= 0 ? 'var(--kft-up)' : 'var(--kft-down)'">{{ h.unrealisedPnlUsd | usd }}</b></span> }
                    </div>
                    <div class="picks">
                      @for (share of shares; track share) {
                        <a (click)="take(h, share)" [title]="h.reducible < abs(h.quantity) ? 'Fill in ' + share + '% of what is not already being closed by a working order' : 'Fill in ' + share + '% of this position'" [attr.data-testid]="'take-' + share">{{ share === 100 ? 'All' : share + '%' }}</a>
                      }
                    </div>
                  </div>
                }
                @if (r.buy || r.sell) {
                  <div class="most">
                    <span>Limits allow</span>
                    @if (r.buy; as side) {
                      <a (click)="takeMost(side)" [title]="'Set by ' + limit(side) + (target()?.[0] === 'm' ? ', on ' + side.venueName : '')" data-testid="max-buy">Buy {{ most(side) | dynamicDecimal }}</a>
                    }
                    @if (r.sell; as side) {
                      <a (click)="takeMost(side)" [title]="'Set by ' + limit(side) + (target()?.[0] === 'm' ? ', on ' + side.venueName : '')" data-testid="max-sell">Sell {{ most(side) | dynamicDecimal }}</a>
                    }
                    @if (style() !== 'Single') {
                      <span title="Each slice of a working order is smaller than one order may be, so only the position limit counts"> (for an order worked over time)</span>
                    }
                  </div>
                }
              </div>
            }
            @if (type() === 'Limit' && style() === 'Single') {
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
              <label nz-checkbox [ngModel]="reduceOnly()" (ngModelChange)="reduceOnly.set($event)" title="The order may shrink a position, never open or flip one" style="margin-bottom:10px">Reduce only</label>
            } @else {
              <span class="hint">Spot: you can sell only what the account holds.</span>
            }
            <div class="field">
              <label>How to work it</label>
              <nz-segmented [nzOptions]="styles" [ngModel]="style()" (ngModelChange)="style.set($event)" data-testid="ticket-style" />
            </div>
            @if (style() !== 'Single') {
              <div class="two">
                @if (style() === 'OverTime') {
                  <div class="field"><label>Over, minutes</label><nz-input-number [ngModel]="durationMinutes()" (ngModelChange)="durationMinutes.set($event)" [nzMin]="1" [nzMax]="1440" style="width:100%" data-testid="ticket-duration" /></div>
                  <div class="field"><label>Slices</label><nz-input-number [ngModel]="sliceCount()" (ngModelChange)="sliceCount.set($event)" [nzMin]="2" [nzMax]="500" style="width:100%" data-testid="ticket-slices" /></div>
                }
                @if (style() === 'Paced') {
                  <div class="field"><label>Slice from, {{ asset() }}</label><nz-input-number [ngModel]="minSlice()" (ngModelChange)="minSlice.set($event)" [nzMin]="0" style="width:100%" data-testid="ticket-min-slice" /></div>
                  <div class="field"><label>Slice to, {{ asset() }}</label><nz-input-number [ngModel]="maxSlice()" (ngModelChange)="maxSlice.set($event)" [nzMin]="0" style="width:100%" data-testid="ticket-max-slice" /></div>
                  <div class="field"><label>Wait between, seconds</label><nz-input-number [ngModel]="waitSeconds()" (ngModelChange)="waitSeconds.set($event)" [nzMin]="1" [nzMax]="3600" style="width:100%" /></div>
                }
                <div class="field" [title]="'Buying waits while the market is above this price, selling while it is below, and carries on when the price comes back'"><label>Pause price</label><nz-input-number [ngModel]="pausePrice()" (ngModelChange)="pausePrice.set($event)" [nzMin]="0" nzPlaceHolder="None" style="width:100%" data-testid="ticket-pause-price" /></div>
                <div class="field" title="The order pauses itself when its average price is this much worse than the price it started at"><label>Largest slippage, bp</label><nz-input-number [ngModel]="maxSlippage()" (ngModelChange)="maxSlippage.set($event)" [nzMin]="1" [nzMax]="5000" nzPlaceHolder="None" style="width:100%" /></div>
              </div>
              <p class="hint" data-testid="style-hint">{{ styleHint() }}</p>
              @if (style() !== 'Paced') {
                <a class="basket-toggle" (click)="basketOpen.set(!basketOpen())" data-testid="basket-toggle">{{ basketOpen() ? 'Send one order instead' : 'Send several at once, as a basket' }}</a>
              }
              @if (basketOpen() && style() !== 'Paced') {
                <div class="field">
                  <label>One order a line, for example: buy 2 BTC-PERP</label>
                  <textarea nz-input rows="5" [ngModel]="basketText()" (ngModelChange)="basketText.set($event)" placeholder="buy 2 BTC-PERP&#10;sell 40 ETH-PERP&#10;buy 500 SOL spot" data-testid="basket-text"></textarea>
                </div>
                @if (basketLines(); as lines) {
                  @if (lines.length > 0) {
                    <div class="basket" data-testid="basket-lines">
                      @for (l of lines; track $index) {
                        <div [class.bad]="l.error !== null">
                          @if (l.error === null) {
                            <b [style.color]="l.side === 'Buy' ? 'var(--kft-up)' : 'var(--kft-down)'">{{ l.side }}</b> {{ l.quantity }} {{ name(l.asset, l.type) }}
                          } @else {
                            Line {{ $index + 1 }}: {{ l.error }}
                          }
                        </div>
                      }
                    </div>
                    <p class="hint" data-testid="basket-hint">
                      All lines are worked the way chosen above ({{ style() === 'OverTime' ? 'over ' + (durationMinutes() ?? 0) + ' minutes in ' + (sliceCount() ?? 0) + ' slices' : 'passive' }}), on the account chosen above, at the market: the limit price and the pause price above are not used for a basket.
                      @if (reduceOnly()) {
                        <b>Reduce only is ticked: every perpetual line may only shrink a position.</b>
                      } @else {
                        Lines may open or add to positions; tick Reduce only above to prevent that for perpetuals.
                      }
                      They are checked together: if one cannot be taken, none is sent.
                    </p>
                    <button nz-button nzType="primary" nzBlock [disabled]="!canSendBasket(d)" [nzLoading]="sending()" nz-popconfirm [nzCondition]="!targetIsLive(d)" [nzPopconfirmTitle]="'Send ' + lines.length + ' real orders to work on a real-money account?'" nzOkText="Send them" (nzOnConfirm)="sendBasket()" data-testid="send-basket">Send the basket of {{ lines.length }}</button>
                  }
                }
              }
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
              <p class="real" data-testid="ticket-live"><b>Real money.</b> A market order goes out priced at the edge of the account's price band, to trade now or not at all.</p>
            }
            @if (!d.canTrade) {
              <p class="note">Your account can look but not trade. Trading needs the Trader or Admin role.</p>
            }
          </div>

          <div class="panel" data-testid="book">
            <h3>
              {{ name(asset(), type2()) }} {{ bookView() === 'Venues' ? 'across venues' : 'depth' }}
              <nz-segmented [nzOptions]="['Venues', 'Depth']" [ngModel]="bookView()" (ngModelChange)="bookView.set($event)" nzSize="small" style="margin-left:6px" data-testid="book-view" />
              <span style="flex:1"></span>
            </h3>
            @if (book.data(); as b) {
              @if (b.spreadBps !== null && b.bestBid !== null && b.bestAsk !== null) {
                <div class="quote" data-testid="quote-strip">
                  <div><span>Best bid</span><b class="up">{{ b.bestBid | number: priceFormat() }}</b><small>{{ venueName(d, b.bestBidVenue) }}</small></div>
                  <div><span>Mid</span><b>{{ (b.bestBid + b.bestAsk) / 2 | number: priceFormat() }}</b><small>{{ depth().quote }}</small></div>
                  <div><span>Best ask</span><b class="down">{{ b.bestAsk | number: priceFormat() }}</b><small>{{ venueName(d, b.bestAskVenue) }}</small></div>
                  <div [title]="b.spreadBps < 0 ? 'The best bid on one venue is above the best ask on another. Fees and transfer time usually explain it.' : 'Between the best bid and the best ask across venues'">
                    <span>{{ b.spreadBps < 0 ? 'Crossed by' : 'Spread' }}</span><b>{{ abs(b.spreadBps) | number: '1.2-2' }} bp</b><small>{{ abs(b.bestAsk - b.bestBid) | number: priceFormat() }} {{ depth().quote }}</small>
                  </div>
                  <div><span>Venues</span><b>{{ b.venues.length }}</b><small>live books</small></div>
                </div>
              }
              @if (b.venues.length === 0) {
                <p class="note" data-testid="book-empty">Opening the book for {{ name(asset(), type2()) }}. It takes a few seconds the first time; if nothing appears, no venue has a live book for it and orders are refused until one does.</p>
              } @else {
                <div class="books">
                  @if (bookView() === 'Venues') {
                  <nz-table [nzData]="b.venues" nzTemplateMode [nzShowPagination]="false" nzSize="small">
                    <thead>
                      <tr><th>Venue</th><th class="num">Bid size</th><th class="num">Bid</th><th class="num">Ask</th><th class="num">Ask size</th><th class="num">Age</th></tr>
                    </thead>
                    <tbody>
                      @for (v of b.venues; track v.venue + v.quote) {
                        <tr>
                          <td>{{ v.venueName }}
                            @if (v.quote !== 'USDT') {
                              <span class="hint" [title]="'Quoted in ' + v.quote + ', not USDT. The two differ by a few hundredths of a percent, so this venue is left out of the best bid and ask above.'">in {{ v.quote }}</span>
                            }
                          </td>
                          <td class="num">{{ v.bidSize ?? 0 | dynamicDecimal }}</td>
                          <td class="num" [class.best]="v.venue === b.bestBidVenue" style="color:#389e0d">{{ v.bestBid ?? 0 | dynamicDecimal }}</td>
                          <td class="num" [class.best]="v.venue === b.bestAskVenue" style="color:#cf1322">{{ v.bestAsk ?? 0 | dynamicDecimal }}</td>
                          <td class="num">{{ v.askSize ?? 0 | dynamicDecimal }}</td>
                          <td class="num hint">{{ v.ageMs | number: '1.0-0' }} ms</td>
                        </tr>
                      }
                    </tbody>
                  </nz-table>
                  } @else {
                  <div class="ladder" data-testid="ladder">
                    <div>
                      <div class="lvl bid head"><span>Venue</span><span>Total</span><span>Size</span><b>Bid</b></div>
                      @for (l of depth().bids; track $index) {
                        <div class="lvl bid"><i [style.width.%]="l.total / depth().most * 100"></i><span>{{ l.venue }}</span><span class="tot">{{ l.total | number: sizeFormat() }}</span><span>{{ l.quantity | number: sizeFormat() }}</span><b>{{ l.price | number: priceFormat() }}</b></div>
                      }
                    </div>
                    <div>
                      <div class="lvl ask head"><b>Ask</b><span>Size</span><span>Total</span><span>Venue</span></div>
                      @for (l of depth().asks; track $index) {
                        <div class="lvl ask"><i [style.width.%]="l.total / depth().most * 100"></i><b>{{ l.price | number: priceFormat() }}</b><span>{{ l.quantity | number: sizeFormat() }}</span><span class="tot">{{ l.total | number: sizeFormat() }}</span><span>{{ l.venue }}</span></div>
                      }
                    </div>
                  </div>
                  <p style="margin:0;font-size:12px;color:var(--kft-text-3)" data-testid="depth-hint">Best {{ depthLevels }} prices each side across {{ depth().venues }} venues quoted in {{ depth().quote }}. The bar is the size building up from the best price.{{ depth().left > 0 ? ' ' + depth().left + ' venue' + (depth().left === 1 ? '' : 's') + ' quoted in another currency ' + (depth().left === 1 ? 'is' : 'are') + ' left out.' : '' }}</p>
                  }
                  @if (rules.data(); as every) {
                    @if (rulesFor(d, every); as listed) {
                      <div class="rules" data-testid="rules">
                        <span class="hint">Smallest steps on the ticket's account{{ listed.length > 1 ? 's' : '' }}:</span>
                        @for (r of listed; track r.venue + r.quote) {
                          <span class="rule" [class.off]="!r.tradable" [title]="r.tradable ? '' : 'This venue lists it but is not taking orders for it'">
                            <b>{{ venueName(d, r.venue) }}{{ r.quote === 'USDT' || r.quote === 'USD' ? '' : ' in ' + r.quote }}</b>
                            price {{ r.priceStep === null ? '?' : (r.priceStep | dynamicDecimal) }} · quantity {{ r.quantityStep === null ? '?' : (r.quantityStep | dynamicDecimal) }}{{ r.minQuantity === null ? '' : ' · from ' + (r.minQuantity | dynamicDecimal) }}
                          </span>
                        }
                      </div>
                    }
                  }
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
            <li><b>Place an order</b> in the order ticket that then appears here, next to the live prices of the venues.</li>
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
          @if (hasPaper(d)) {
            <label nz-checkbox class="inbook" [ngModel]="d.paperInBook" [nzDisabled]="!d.canAdmin" (ngModelChange)="run(http.post('/api/funds/' + d.fundId + '/paper-in-book', { include: $event }), 'Saved.')" data-testid="paper-in-book"
              title="On: what the paper accounts hold, and their balance, show in Holdings, exposure, risk and NAV. Their value comes in and goes out as money, not as a gain or a loss. Real-money accounts always count.">Count paper accounts in the fund's book</label>
          }
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
            @if (editing() === null && (f.venue === 'Binance' || f.venue === 'Bybit')) {
              <div class="field" title="One account trades one family of perpetuals. A manager who wants both has two accounts. USDC-margined accounts trade on paper for now."><label>Perpetuals in</label>
                <nz-select [ngModel]="f.perpQuote" (ngModelChange)="patch({ perpQuote: $event })" style="width:100px" data-testid="account-perp-quote"><nz-option nzValue="USDT" nzLabel="USDT" /><nz-option nzValue="USDC" nzLabel="USDC" /></nz-select>
              </div>
            }
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
                <td><b>{{ a.manager }}</b> · {{ a.venueName }}{{ a.perpQuote === 'USDT' ? '' : ' · ' + a.perpQuote + ' perps' }} <span class="hint"><app-edit-name [value]="a.label" [url]="d.canAdmin ? '/api/trading-accounts/' + a.id + '/label' : null" what="this trading account" [maxlength]="100" (saved)="again()" /> · {{ a.sleeve }}</span>
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
              <tr><td colspan="8" style="text-align:center;color:var(--kft-text-3);padding:22px">No trading account yet. {{ d.canAdmin ? 'Add one to start paper trading.' : 'An administrator adds them.' }}</td></tr>
            }
          </tbody>
        </nz-table>
      </div>

      @if (working.data(); as list) {
        @if (list.length > 0) {
          <div class="panel" data-testid="working-orders">
            <h3>Working orders <span class="hint">Large orders worked over time. Each slice is an ordinary order, checked and filled like any other.</span></h3>
            <nz-table [nzData]="list" nzTemplateMode [nzShowPagination]="false" nzSize="small">
              <thead>
                <tr><th>Order</th><th>How</th><th>Where</th><th style="width:190px">Filled</th><th class="num">Average</th><th class="num">Slippage</th><th class="num">Fees</th><th>State</th><th></th></tr>
              </thead>
              <tbody>
                @for (w of list; track w.id) {
                  <tr class="rowlink" (click)="openWorking.set(openWorking() === w.id ? null : w.id)" [attr.data-testid]="'working-' + w.id">
                    <td class="nowrap"><b [style.color]="w.side === 'Buy' ? 'var(--kft-up)' : 'var(--kft-down)'">{{ w.side }}</b> {{ w.quantity | dynamicDecimal }} {{ name(w.asset, w.instrumentType) }}{{ w.limitPrice === null ? '' : (w.side === 'Buy' ? ' up to ' : ' down to ') + (w.limitPrice | dynamicDecimal) }} @if (w.reduceOnly) { <span class="hint">reduce only</span> }</td>
                    <td class="nowrap">{{ how(w) }} @if (w.basket) { <span class="hint" [title]="'Sent together with other lines as one basket'">basket</span> }</td>
                    <td class="nowrap">{{ w.manager }} · {{ w.where }} @if (w.isPaper) { <span class="hint">paper</span> }</td>
                    <td>
                      <div class="prog"><i [style.width.%]="w.quantity > 0 ? w.filledQuantity / w.quantity * 100 : 0"></i></div>
                      <span class="hint">{{ w.filledQuantity | dynamicDecimal }} of {{ w.quantity | dynamicDecimal }} · {{ w.slicesSent }} slices</span>
                    </td>
                    <td class="num">{{ w.filledQuantity > 0 ? (w.averageFillPrice | dynamicDecimal) : '–' }}</td>
                    <td class="num" [title]="w.arrivalPrice === null ? '' : 'Against ' + w.arrivalPrice + ', the middle of the market when it started'">{{ w.slippageBps === null ? '–' : (w.slippageBps | number: '1.1-1') + ' bp' }}</td>
                    <td class="num">{{ w.feesUsd | usd }}</td>
                    <td>
                      <nz-tag [nzColor]="workingColor[w.state]">{{ w.stopping ? 'Stopping' : workingLabel[w.state] }}</nz-tag>
                      @if (w.note) {
                        <div class="hint wnote">{{ w.note }}</div>
                      }
                    </td>
                    <td class="nowrap" style="text-align:right" (click)="$event.stopPropagation()">
                      @if (d.canTrade) {
                        @if (w.state === 'Working' && !w.stopping) {
                          <a (click)="run(http.post('/api/working-orders/' + w.id + '/pause', {}), 'Paused.')" [attr.data-testid]="'pause-' + w.id">Pause</a>
                        }
                        @if (w.state === 'Paused' && !w.stopping) {
                          <a (click)="run(http.post('/api/working-orders/' + w.id + '/resume', {}), 'Resumed.')" [attr.data-testid]="'resume-' + w.id">Resume</a>
                        }
                        @if ((w.state === 'Working' || w.state === 'Paused' || w.state === 'PendingApproval') && !w.stopping) {
                          <a style="margin-left:12px;color:#cf1322" nz-popconfirm nzPopconfirmTitle="Stop this order? What has been filled stays filled." nzOkText="Stop it" (nzOnConfirm)="run(http.post('/api/working-orders/' + w.id + '/stop', {}), 'Stopping.')" [attr.data-testid]="'stop-' + w.id">Stop</a>
                        }
                      }
                    </td>
                  </tr>
                  @if (openWorking() === w.id) {
                    <tr>
                      <td colspan="9" class="slices" [attr.data-testid]="'slices-' + w.id">
                        @if (workingDetail.data(); as detail) {
                          <div class="hint" style="margin-bottom:6px">
                            Sent by {{ detail.createdBy }} {{ detail.createdAt | date: 'd MMM HH:mm' : 'UTC' }} UTC{{ detail.approvedBy ? ', approved by ' + detail.approvedBy : '' }}
                            @if (detail.startedAt) { · started {{ detail.startedAt | date: 'HH:mm:ss' : 'UTC' }} }
                            @if (detail.plannedEndAt) { · due to finish {{ detail.plannedEndAt | date: 'HH:mm' : 'UTC' }} }
                            @if (detail.hardEndAt && !detail.endedAt) { · stops by itself at {{ detail.hardEndAt | date: 'd MMM HH:mm' : 'UTC' }} UTC }
                            @if (detail.endedAt) { · ended {{ detail.endedAt | date: 'd MMM HH:mm:ss' : 'UTC' }} UTC }
                            @if (detail.pausePrice !== null) { · pause price {{ detail.pausePrice | dynamicDecimal }} }
                            @if (detail.maxSlippageBps !== null) { · slippage limit {{ detail.maxSlippageBps }} bp }
                          </div>
                          @if ((detail.slices ?? []).length === 0) {
                            <span class="hint">No slice has been sent yet.</span>
                          } @else {
                            <table class="mini">
                              <tr><th>Time (UTC)</th><th>Venue</th><th class="num">Quantity</th><th class="num">Filled</th><th class="num">Price</th><th class="num">Fee</th><th>Status</th></tr>
                              @for (s of detail.slices ?? []; track s.id) {
                                <tr>
                                  <td>{{ s.at | date: 'HH:mm:ss' : 'UTC' }}</td><td>{{ s.venueName }}</td><td class="num">{{ s.quantity | dynamicDecimal }}</td><td class="num">{{ s.filledQuantity | dynamicDecimal }}</td>
                                  <td class="num">{{ s.filledQuantity > 0 ? (s.averageFillPrice | dynamicDecimal) : s.limitPrice !== null ? 'rests at ' + (s.limitPrice | dynamicDecimal) : '–' }}</td>
                                  <td class="num">{{ s.feesUsd | usd }}</td>
                                  <td><nz-tag [nzColor]="statusColor[s.status]">{{ statusLabel[s.status] }}</nz-tag> @if (s.rejectReason) { <span class="hint">{{ s.rejectReason }}</span> }</td>
                                </tr>
                              }
                            </table>
                          }
                        }
                      </td>
                    </tr>
                  }
                }
              </tbody>
            </nz-table>
          </div>
        }
      }

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
              <tr><td colspan="10" style="text-align:center;color:var(--kft-text-3);padding:22px">{{ openOnly() ? 'No open orders.' : 'No orders yet.' }}</td></tr>
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
      color: var(--kft-text-3);
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
      color: var(--kft-text-3);
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

    .inbook {
      margin-right: 14px;
      font-size: 12.5px;
      font-weight: 400;
    }
    .prog {
      height: 6px;
      border-radius: 99px;
      background: var(--kft-fill);
      overflow: hidden;
      margin: 4px 0 3px;
    }
    .prog i {
      display: block;
      height: 100%;
      background: var(--kft-brand);
      border-radius: 99px;
    }
    .rowlink {
      cursor: pointer;
    }
    .wnote {
      max-width: 340px;
      white-space: normal;
      margin-top: 3px;
    }
    .slices {
      background: var(--kft-bg);
      padding: 10px 16px 12px 28px !important;
    }
    .mini {
      width: 100%;
      border-collapse: collapse;
      font-size: 12.5px;
    }
    .mini th {
      text-align: left;
      font-weight: 600;
      color: var(--kft-text-3);
      font-size: 11px;
      padding: 3px 8px;
    }
    .mini td {
      padding: 4px 8px;
      border-top: 1px solid var(--kft-line);
    }
    .mini .num {
      text-align: right;
    }
    .basket-toggle {
      display: block;
      font-size: 12.5px;
      margin: -4px 0 10px;
    }
    .basket {
      font-size: 12.5px;
      border: 1px solid var(--kft-line);
      border-radius: 8px;
      padding: 6px 10px;
      margin-bottom: 8px;
    }
    .basket .bad {
      color: var(--kft-down);
    }
    .room {
      margin: -2px 0 12px;
      font-size: 12.5px;
    }
    .held {
      background: var(--kft-bg);
      border: 1px solid var(--kft-line);
      border-radius: 8px;
      padding: 8px 10px;
      margin-bottom: 6px;
      font-variant-numeric: tabular-nums;
    }
    .held .line {
      display: flex;
      align-items: baseline;
      gap: 6px;
    }
    .held .line.sub {
      color: var(--kft-text-3);
      font-size: 12px;
      margin-top: 1px;
    }
    .held .worth {
      margin-left: auto;
      font-weight: 600;
    }
    .held .sub .worth {
      font-weight: 400;
    }
    .held .side {
      font-size: 11px;
      font-weight: 600;
      padding: 0 6px;
      border-radius: 4px;
      background: var(--kft-up-soft);
      color: var(--kft-up);
    }
    .held .side.short {
      background: var(--kft-down-soft);
      color: var(--kft-down);
    }
    .room .picks {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 6px;
      margin-top: 8px;
    }
    .room .picks a {
      text-align: center;
      font-weight: 500;
      font-size: 12px;
      padding: 2px 0;
      border: 1px solid var(--kft-line);
      border-radius: 6px;
      background: var(--kft-card);
      color: var(--kft-text-2);
    }
    .room .picks a:hover {
      border-color: var(--kft-brand);
      color: var(--kft-brand);
    }
    .most {
      display: flex;
      gap: 10px;
      align-items: baseline;
      font-size: 12px;
      color: var(--kft-text-3);
      padding: 0 2px;
    }
    .most a {
      font-weight: 500;
    }
    .real {
      margin: 10px 0 0;
      padding: 8px 10px;
      border-radius: 8px;
      background: var(--kft-down-soft);
      color: var(--kft-down);
      font-size: 12px;
      line-height: 1.45;
    }
    .quote {
      display: grid;
      grid-template-columns: repeat(5, 1fr);
      border: 1px solid var(--kft-line);
      border-radius: 10px;
      margin-bottom: 12px;
      font-variant-numeric: tabular-nums;
    }
    .quote div {
      padding: 8px 12px;
      border-right: 1px solid var(--kft-line);
      display: grid;
      gap: 1px;
    }
    .quote div:last-child {
      border-right: 0;
    }
    .quote span {
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--kft-text-3);
    }
    .quote b {
      font-size: 16px;
      font-weight: 600;
      color: var(--kft-text);
    }
    .quote b.up {
      color: #389e0d;
    }
    .quote b.down {
      color: #cf1322;
    }
    .quote small {
      font-size: 11.5px;
      color: var(--kft-text-3);
    }
    .decided td {
      color: var(--kft-text-3);
    }
    .quick {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      align-items: center;
      margin: -4px 0 12px;
    }
    .pick {
      padding: 2px 9px;
      border-radius: 6px;
      background: var(--kft-fill);
      color: var(--kft-text-2);
      font-size: 12px;
      font-weight: 500;
      cursor: pointer;
    }
    .pick.pin {
      background: var(--kft-brand-soft);
      color: var(--kft-brand);
    }
    .pick.on {
      outline: 1.5px solid var(--kft-brand);
    }
    .pinner {
      margin-left: auto;
      font-size: 12px;
    }
    .rules {
      display: flex;
      flex-wrap: wrap;
      gap: 6px 14px;
      margin-top: 12px;
      font-size: 12px;
      color: var(--kft-text-2);
    }
    .rule b {
      font-weight: 600;
      color: var(--kft-text);
    }
    .rule.off {
      opacity: 0.5;
    }
    .venue.open {
      color: var(--kft-text-3);
    }
    .field {
      margin-bottom: 10px;
    }

    .field label {
      display: block;
      font-size: 12px;
      color: var(--kft-text-3);
      margin-bottom: 3px;
    }

    .two {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 10px;
    }

    .est {
      margin: 10px 0;
      color: var(--kft-text-2);
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
      color: var(--kft-text-2);
      line-height: 1.9;
    }

    .note {
      color: var(--kft-text-3);
      font-size: 12px;
      margin: 8px 0 0;
    }

    .form {
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      align-items: flex-start;
      background: #fafbfc;
      border: 1px solid var(--kft-line);
      border-radius: 8px;
      padding: 12px;
      margin-bottom: 12px;
    }

    .form .field {
      margin-bottom: 0;
    }

    .pos td {
      background: #fafbfc;
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
      gap: 2px;
      font-size: 12px;
      font-variant-numeric: tabular-nums;
      border: 1px solid var(--kft-line);
      border-radius: 10px;
      overflow: hidden;
    }

    .lvl {
      position: relative;
      display: grid;
      grid-template-columns: 1.1fr 1fr 1fr 1.1fr;
      padding: 3px 12px;
      line-height: 18px;
    }

    .lvl > span,
    .lvl > b {
      position: relative;
      text-align: right;
    }

    .lvl.head {
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--kft-text-3);
      background: var(--kft-bg);
      border-bottom: 1px solid var(--kft-line);
      padding: 5px 12px;
    }

    .lvl.head b {
      font-weight: 500;
      color: var(--kft-text-3) !important;
    }

    .lvl:not(.head):hover {
      background: var(--kft-line-soft);
    }

    .lvl .tot {
      color: var(--kft-text-3);
    }

    .lvl i {
      position: absolute;
      top: 1px;
      bottom: 1px;
      opacity: 0.12;
    }

    .lvl.bid > span:first-of-type {
      text-align: left;
      color: var(--kft-text-3);
    }

    .lvl.bid i {
      right: 0;
      background: #389e0d;
    }

    .lvl.bid b {
      color: #389e0d;
    }

    .lvl.ask > b {
      text-align: left;
    }

    .lvl.ask > span:last-of-type {
      color: var(--kft-text-3);
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

  // Working orders.
  protected readonly working = keptData(this.fundId$, this.beat, (id) => this.http.get<WorkingOrder[]>(`/api/funds/${id}/working-orders`), (id) => id);
  protected readonly openWorking = signal<number | null>(null);
  protected readonly workingDetail = keptData(toObservable(this.openWorking), this.beat, (id) => (id === null ? of(null) : this.http.get<WorkingOrder>(`/api/working-orders/${id}`)), (id) => id);
  protected readonly workingLabel = WORKING_STATE_LABEL;
  protected readonly workingColor = WORKING_STATE_COLOR;
  protected readonly styles = [
    { label: 'Single', value: 'Single' },
    { label: 'Over time', value: 'OverTime' },
    { label: 'Paced', value: 'Paced' },
    { label: 'Passive', value: 'Passive' },
  ];
  protected readonly style = signal<'Single' | WorkingStyle>('Single');
  protected readonly durationMinutes = signal<number | null>(60);
  protected readonly sliceCount = signal<number | null>(12);
  protected readonly minSlice = signal<number | null>(null);
  protected readonly maxSlice = signal<number | null>(null);
  protected readonly waitSeconds = signal<number | null>(30);
  protected readonly pausePrice = signal<number | null>(null);
  protected readonly maxSlippage = signal<number | null>(null);

  // The basket: several orders typed one a line.
  protected readonly basketOpen = signal(false);
  protected readonly basketText = signal('');
  protected readonly basketLines = computed(() =>
    this.basketText()
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line !== '')
      .map((line) => FundTradingPage.readLine(line)),
  );

  /** Reads "buy 2 BTC-PERP" or "sell 500 SOL spot", in any order of its words. */
  private static readLine(line: string): { side: OrderSide; quantity: number; asset: string; type: TradedType; error: string | null } {
    const words = line.split(/\s+/);
    const sides = words.filter((w) => /^(buy|sell)$/i.test(w));
    const numbers = words.filter((w) => /^\d*\.?\d+$/.test(w));
    const sideWord = sides[0];
    const numberWord = numbers[0];
    const rest = words.filter((w) => !sides.includes(w) && !numbers.includes(w));
    const spot = rest.some((w) => /^spot$/i.test(w));
    const names = rest.filter((w) => !/^(spot|perp)$/i.test(w));
    const named = names[0] ?? '';
    // A comma could mean thousands or decimals, and guessing wrong is ten or a thousand times the size.
    const unclear = /,/.test(line) ? 'write the quantity without commas, with a point for decimals.' : sides.length > 1 || numbers.length > 1 || names.length > 1 ? 'one order a line: one of buy or sell, one quantity, one instrument.' : null;
    const perp = /-PERP$/i.test(named) || rest.some((w) => /^perp$/i.test(w));
    const asset = named.replace(/-PERP$/i, '').toUpperCase();
    const quantity = Number(numberWord);
    const error =
      unclear !== null ? unclear : sideWord === undefined ? 'say buy or sell.' : numberWord === undefined || !(quantity > 0) ? 'give a quantity.' : !/^[A-Z0-9]{1,20}$/.test(asset) ? 'name the instrument, like BTC-PERP or SOL spot.' : spot === perp ? 'say whether it is spot or a perpetual, like BTC-PERP or SOL spot.' : null;
    return { side: /^buy$/i.test(sideWord ?? '') ? 'Buy' : 'Sell', quantity, asset, type: perp ? 'Perp' : 'Spot', error };
  }

  protected canSendBasket(d: Trading): boolean {
    const lines = this.basketLines();
    return d.canTrade && this.target() !== null && lines.length >= 2 && lines.every((l) => l.error === null);
  }

  protected sendBasket(): void {
    const target = this.target();
    if (target === null || this.sending()) {
      return;
    }

    const lines = this.basketLines().map((l) => ({
      tradingAccountId: target[0] === 'a' ? Number(target.slice(1)) : null,
      managerId: target[0] === 'm' ? Number(target.slice(1)) : null,
      asset: l.asset,
      instrumentType: l.type,
      side: l.side,
      quantity: l.quantity,
      limitPrice: null,
      reduceOnly: l.type === 'Perp' && this.reduceOnly(),
      style: this.style(),
      durationMinutes: this.durationMinutes(),
      slices: this.sliceCount(),
      minSlice: null,
      maxSlice: null,
      waitSeconds: null,
      pausePrice: null,
      maxSlippageBps: this.maxSlippage(),
    }));
    this.sending.set(true);
    const what = 'basket:' + JSON.stringify(lines);
    this.run(this.http.post<Answer>('/api/working-orders/basket', { lines, clientId: this.nameFor(what).slice(0, 40) }), `Basket of ${lines.length} recorded.`, () => this.sending.set(false), () => {
      this.attempts.delete(what);
      this.basketText.set('');
      this.basketOpen.set(false);
    });
  }

  /** In words, what the chosen way of working will do with the quantity entered. */
  protected readonly styleHint = computed(() => {
    const quantity = this.quantity() ?? 0;
    switch (this.style()) {
      case 'OverTime': {
        const minutes = this.durationMinutes() ?? 0;
        const slices = this.sliceCount() ?? 0;
        return slices > 0 && minutes > 0 ? `${slices} slices of about ${+(quantity / slices).toPrecision(4)} ${this.asset()}, one every ${+((minutes * 60) / slices).toFixed(0)} seconds. Each goes to the venue with the best price at that moment.` : '';
      }
      case 'Paced':
        return 'Slices of a size picked at random between the two, with the wait between them, until it is all done or a day has passed.';
      case 'Passive':
        return 'Rests at the best price as a maker and follows the market. It never crosses the spread, so it may take a while or not fill at all.';
      default:
        return '';
    }
  });

  protected how(w: WorkingOrder): string {
    switch (w.style) {
      case 'OverTime':
        return `Over ${Math.round((w.durationSeconds ?? 0) / 60)} min in ${w.sliceCount} slices`;
      case 'Paced':
        return `Paced, ${w.minSlice}–${w.maxSlice} every ${w.waitSeconds} s`;
      default:
        return 'Passive, at the best price';
    }
  }

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
  protected readonly sending = signal(false);

  /** What each venue's list says about the chosen instrument. Read once per instrument: it changes hourly at most. */
  protected readonly rules = keptData(toObservable(this.instrument), of(0), (i) => this.http.get<VenueRules[]>(`/api/instruments/${i.split(':')[0]}/${encodeURIComponent(i.split(':')[1])}`), (i) => i);

  /** Requests still waiting for a decision, the oldest first. */
  protected waiting(d: Trading): Trading['approvals'] {
    return d.approvals.filter((a) => a.status === 'Pending').reverse();
  }

  /** The latest requests that have been decided, so that what happened to one just approved can be seen. */
  protected decided(d: Trading): Trading['approvals'] {
    return d.approvals.filter((a) => a.status !== 'Pending').slice(0, 5);
  }

  /**
   * The steps that matter for the order about to be sent: those of the venue the ticket's account is on, in the
   * currency it trades this instrument in. For a manager's "best venue", those of each of the manager's accounts.
   * Null when there are none to show.
   */
  protected rulesFor(d: Trading, every: VenueRules[]): VenueRules[] | null {
    const target = this.target();
    const accounts = d.accounts.filter((a) => (target?.[0] === 'a' ? 'a' + a.id === target : 'm' + a.managerId === target));
    const shown = every.filter((r) =>
      accounts.some((a) => {
        const sameVenue = every.filter((x) => x.venue === a.venue);
        const wanted = this.type2() === 'Perp' && a.perpQuote === 'USDC' ? 'USDC' : 'USDT';
        // The account's own market on its venue; where the venue has only one, that one.
        return r.venue === a.venue && (sameVenue.length === 1 || r.quote === wanted);
      }),
    );
    return shown.length > 0 ? shown : null;
  }

  protected hasPaper(d: Trading): boolean {
    return d.accounts.some((a) => a.mode === 'Paper');
  }

  protected isPinned(d: Trading): boolean {
    return d.pinned.some((p) => p.asset === this.asset() && p.type === this.type2());
  }

  /** Recently traded instruments that are not already shown as pins. */
  protected unpinnedRecent(d: Trading): InstrumentRef[] {
    return d.recent.filter((r) => !d.pinned.some((p) => p.asset === r.asset && p.type === r.type));
  }

  protected pin(d: Trading): void {
    this.run(this.http.post(`/api/funds/${d.fundId}/pins`, { asset: this.asset(), type: this.type2() }), 'Pinned. Its book now stays open.');
  }

  protected unpin(d: Trading): void {
    this.run(this.http.delete(`/api/funds/${d.fundId}/pins/${this.type2()}/${encodeURIComponent(this.asset())}`), 'Unpinned.');
  }

  /** What the ticket's account holds of the chosen instrument, and the most it may buy or sell. */
  protected readonly room = keptData(
    combineLatest([toObservable(this.instrument), toObservable(this.target)]).pipe(filter(([, target]) => target !== null)),
    this.beat,
    ([i, target]) => this.http.get<Room>(`/api/room/${i.split(':')[0]}/${encodeURIComponent(i.split(':')[1])}`, { params: target![0] === 'a' ? { account: target!.slice(1) } : { manager: target!.slice(1) } }),
    ([i, target]) => i + target,
  );
  protected readonly shares = [25, 50, 75, 100];

  /** The largest order for the way of working chosen: one order, or one worked over time. */
  protected most(side: RoomSide): number {
    return this.style() === 'Single' ? side.max : side.maxWorking;
  }

  /** Fills in the most the limits allow. That is an order that may open or add to a position, so it is not one that only reduces. */
  protected takeMost(side: RoomSide): void {
    let most = this.most(side);
    // A limit order is sized at its limit price. Above the market, the same dollars buy fewer units.
    const mid = this.book.data()?.mid ?? null;
    const limit = this.type() === 'Limit' ? this.limitPrice() : null;
    if (mid !== null && limit !== null && limit > mid) {
      const step = this.room.data()?.quantityStep ?? null;
      most = (most * mid) / limit;
      most = step !== null && step > 0 ? Math.floor(most / step + 1e-9) * step : Math.floor(most * 1e8) / 1e8;
    }

    this.quantity.set(+most.toFixed(10));
    this.reduceOnly.set(false);
    if (this.target()?.[0] === 'm') {
      // The figure is one account's. The order goes to that account, so that it is checked against the limits it was worked out from.
      this.target.set('a' + side.tradingAccountId);
    }
  }

  protected limit(side: RoomSide): string {
    return this.style() === 'Single' || side.maxWorking === side.max ? side.limitedBy : 'the position limit';
  }

  /** Fills in a share of a position, rounded down to the venue's step, and for a perpetual marks the order as one that only reduces. */
  protected take(held: Held, share: number): void {
    // Of what can still be taken off: orders already working to close it are counted, as they are when an order is checked.
    const wanted = (held.reducible * share) / 100;
    const step = held.quantityStep;
    // All of it is all of it: rounding must not leave dust behind when the whole position is asked for. It is
    // cut, never rounded up, so that the order is not for a hair more than is held.
    const quantity = share === 100 || step === null || step <= 0 ? Math.floor(wanted * 1e8 + 1e-6) / 1e8 : Math.floor(wanted / step + 1e-9) * step;
    this.quantity.set(+quantity.toFixed(10));
    if (this.type2() === 'Perp') {
      this.reduceOnly.set(true);
    }

    if (this.target()?.[0] === 'm') {
      // The position is on one account; an order to reduce it has to go there, not to whichever venue is cheapest.
      this.target.set('a' + held.tradingAccountId);
    }
  }

  protected readonly book = keptData(toObservable(this.instrument), this.beat, (i) => this.http.get<Book>(`/api/book/${i.split(':')[0]}/${encodeURIComponent(i.split(':')[1])}`), (i) => i);
  protected readonly notional = computed(() => (this.quantity() ?? 0) * ((this.type() === 'Limit' ? this.limitPrice() : null) ?? this.book.data()?.mid ?? 0));

  /** Every venue's levels in one ladder, best first, with a running total for the bars. */
  /** Which of the two ways the book is shown: each venue's best prices, or the depth of all of them together. */
  protected readonly bookView = signal<'Venues' | 'Depth'>('Venues');

  /**
   * The depth across venues, in one currency: the one the ticket's account trades this instrument in, or USDT, or
   * whatever there is. A book in another currency is left out, because a price in USDC is not a better or worse
   * price than one in USDT; it is a price for something slightly different.
   */
  /** How many prices each side of the ladder shows. */
  protected readonly depthLevels = 15;

  /** One number of decimals for every price on the panel, so the column lines up: as many as the finest price shown needs. */
  protected readonly priceFormat = computed(() => {
    const b = this.book.data();
    const prices = [b?.bestBid, b?.bestAsk, ...this.depth().bids.map((l) => l.price), ...this.depth().asks.map((l) => l.price)].filter((x): x is number => typeof x === 'number');
    const digits = Math.min(8, Math.max(0, ...prices.map(decimals)));
    return `1.${digits}-${digits}`;
  });

  /** The same for sizes, to four decimals at most. */
  protected readonly sizeFormat = computed(() => {
    const digits = Math.min(4, Math.max(0, ...[...this.depth().bids, ...this.depth().asks].map((l) => decimals(l.quantity))));
    return `1.${digits}-${digits}`;
  });

  protected readonly depth = computed(() => {
    const all = this.book.data()?.venues ?? [];
    const target = this.target();
    const account = target?.[0] === 'a' ? this.page.data()?.accounts.find((a) => 'a' + a.id === target) : undefined;
    const wanted = this.type2() === 'Perp' && account?.perpQuote === 'USDC' ? 'USDC' : 'USDT';
    const quote = all.some((v) => v.quote === wanted) ? wanted : all.some((v) => v.quote === 'USDT') ? 'USDT' : (all[0]?.quote ?? wanted);
    const venues = all.filter((v) => v.quote === quote);
    const side = (pick: 'bids' | 'asks'): Depth[] => {
      let total = 0;
      return venues
        .flatMap((v) => v[pick].map((l) => ({ venue: v.venueName, price: l.price, quantity: l.quantity })))
        .sort((a, b) => (pick === 'bids' ? b.price - a.price : a.price - b.price))
        .slice(0, this.depthLevels)
        .map((l) => ({ ...l, total: (total += l.quantity) }));
    };
    const bids = side('bids');
    const asks = side('asks');
    return { bids, asks, most: Math.max(bids.at(-1)?.total ?? 0, asks.at(-1)?.total ?? 0, 1e-12), quote, venues: venues.length, left: all.length - venues.length };
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

  /** What a Trade link on another page asked the ticket to open on; used once, when the page's data first arrives. */
  private asked: { instrument: string | null; manager: number | null } | null = (() => {
    const query = inject(ActivatedRoute).snapshot.queryParamMap;
    const manager = Number(query.get('manager'));
    return query.has('instrument') || query.has('manager') ? { instrument: query.get('instrument'), manager: Number.isInteger(manager) && manager > 0 ? manager : null } : null;
  })();

  constructor() {
    // A reduce-only tick, a limit price and a pause price are about one instrument. On another they would
    // refuse a normal order, or hold a working one at a price that means nothing there.
    let shown: string | null = null;
    effect(() => {
      const instrument = this.instrument();
      if (shown !== null && shown !== instrument) {
        this.reduceOnly.set(false);
        this.limitPrice.set(null);
        this.pausePrice.set(null);
        this.quantity.set(null);
      }

      shown = instrument;
    });

    effect(() => {
      const d = this.page.data();
      if (d === null) {
        return;
      }

      // The menu on the left follows the fund this page is about.
      this.funds.open(d.fundId);
      // Arriving from a position's Trade link: the ticket opens on that instrument, and on that manager's account.
      const asked = this.asked;
      if (asked !== null) {
        this.asked = null;
        if (/^(Spot|Perp):[A-Z0-9]{1,20}$/.test(asked.instrument ?? '')) {
          this.instrument.set(asked.instrument!);
        }

        const own = d.accounts.filter((a) => a.managerId === asked.manager);
        if (own.length === 1) {
          this.target.set('a' + own[0].id);
        } else if (own.length > 1) {
          // Several accounts: the system picks the venue with the best price, unless the person chooses one.
          this.target.set('m' + asked.manager);
        } else if (asked.manager !== null) {
          const manager = d.managers.find((m) => m.id === asked.manager);
          this.error.set(`${manager?.name ?? 'That manager'} has no trading account yet, so the ticket is on another account. ${d.canAdmin ? 'Add one under Trading accounts below.' : 'An administrator can add one.'}`);
        }
      }

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
    // What the search found in the venues' lists and has no open book yet. Choosing one opens its book.
    const found = this.found().filter((i) => (venue === undefined || i.venues.includes(venue)) && !live.some((l) => l.asset === i.asset && l.type === i.type));
    const all = [...live, ...found];
    const chosen = { asset: this.asset(), type: this.type2() };
    return all.some((i) => i.asset === chosen.asset && i.type === chosen.type) ? all : [chosen, ...all];
  }

  /** Instruments the last search found in the venues' lists. */
  private readonly found = signal<{ asset: string; type: TradedType; venues: TradingVenue[] }[]>([]);
  private searching: ReturnType<typeof setTimeout> | undefined;
  private searches = 0;

  /** Looks the typed text up in every venue's list, a moment after the typing stops. */
  protected search(text: string): void {
    clearTimeout(this.searching);
    const typed = text.trim();
    if (typed === '') {
      return;
    }

    const asked = ++this.searches;
    this.searching = setTimeout(() => {
      this.http.get<{ asset: string; type: TradedType; venues: TradingVenue[] }[]>('/api/instruments', { params: { q: typed } }).subscribe({
        // An answer to an earlier search that arrives late does not replace a later one's.
        next: (hits) => asked === this.searches && this.found.set(hits),
        error: () => undefined,
      });
    }, 200);
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

    if (this.style() !== 'Single') {
      this.place(
        {
          tradingAccountId: target[0] === 'a' ? Number(target.slice(1)) : null,
          managerId: target[0] === 'm' ? Number(target.slice(1)) : null,
          asset: this.asset(),
          instrumentType: this.type2(),
          side,
          quantity: this.quantity(),
          limitPrice: this.type() === 'Limit' ? this.limitPrice() : null,
          reduceOnly: this.type2() === 'Perp' && this.reduceOnly(),
          style: this.style(),
          durationMinutes: this.durationMinutes(),
          slices: this.sliceCount(),
          minSlice: this.minSlice(),
          maxSlice: this.maxSlice(),
          waitSeconds: this.waitSeconds(),
          pausePrice: this.pausePrice(),
          maxSlippageBps: this.maxSlippage(),
        },
        '/api/working-orders',
        'clientId',
      );
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

  /** The order being sent and the name it goes under, kept until the server has answered for it. */
  /**
   * The name each thing being sent goes under, kept until the server has answered for it. Sending something else in
   * between does not lose it: a retry of the first still goes under its first name, and the server returns the first.
   */
  private readonly attempts = new Map<string, string>();

  private nameFor(what: string): string {
    let name = this.attempts.get(what);
    if (name === undefined) {
      name = `ui-${crypto.randomUUID()}`;
      this.attempts.set(what, name);
    }

    return name;
  }

  private place(order: Record<string, unknown>, url = '/api/orders', nameField = 'clientOrderId'): void {
    // A second click while the first is still on its way does nothing.
    if (this.sending()) {
      return;
    }

    this.sending.set(true);
    // The name makes a repeated send the same order, not a second one. It is kept until the server answers: if the
    // answer is lost and the same order is sent again, it goes under the same name and the server returns the first.
    const what = url + ':' + JSON.stringify(order);
    const body = { ...order, [nameField]: this.nameFor(what) };
    this.run(this.http.post<Answer>(url, body), url === '/api/orders' ? `${order['side']} ${order['quantity']} ${order['asset']} sent.` : `Working order to ${String(order['side']).toLowerCase()} ${order['quantity']} ${order['asset']} recorded.`, () => this.sending.set(false), () => this.attempts.delete(what));
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
    this.form.set({ managerId: d.managers[0]?.id ?? null, venue: 'Binance', label: 'Main', leverage: 3, marginMode: 'Cross', maxOrderNotionalUsd: 250_000, maxPositionNotionalUsd: 1_000_000, priceBandBps: 100, paperBalanceUsd: 1_000_000, perpQuote: 'USDT' });
  }

  protected startEdit(a: TradingAccount): void {
    this.editing.set(a.id);
    this.keyAccount.set(null);
    this.form.set({ managerId: a.managerId, venue: a.venue, label: a.label, leverage: a.leverage, marginMode: a.marginMode, maxOrderNotionalUsd: a.maxOrderNotionalUsd, maxPositionNotionalUsd: a.maxPositionNotionalUsd, priceBandBps: a.priceBandBps, paperBalanceUsd: a.paperBalanceUsd, perpQuote: a.perpQuote });
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
