import { Injectable, inject } from '@angular/core';
import { Action, Selector, State, StateContext } from '@ngxs/store';
import { tap } from 'rxjs';
import { FundsApi } from './funds.api';
import { FundListItem, InstrumentFilter, SleeveRef } from './models';

export class LoadFunds {
  static readonly type = '[View] Load funds';
}

export class SetInstrumentFilter {
  static readonly type = '[View] Set instrument filter';
  constructor(readonly filter: InstrumentFilter) {}
}

/** Remembers the sleeves of the fund being looked at, for the navigation. */
export class SetFundSleeves {
  static readonly type = '[View] Set fund sleeves';
  constructor(
    readonly fundId: number,
    readonly sleeves: SleeveRef[],
  ) {}
}

export interface ViewStateModel {
  funds: FundListItem[] | null;
  filter: InstrumentFilter;
  fundId: number | null;
  sleeves: SleeveRef[];
}

/** What stays the same while the user moves between pages: the fund list, the instrument filter, the current fund. */
@State<ViewStateModel>({ name: 'view', defaults: { funds: null, filter: 'All', fundId: null, sleeves: [] } })
@Injectable()
export class ViewState {
  private readonly api = inject(FundsApi);

  @Selector()
  static funds(state: ViewStateModel): FundListItem[] | null {
    return state.funds;
  }

  @Selector()
  static filter(state: ViewStateModel): InstrumentFilter {
    return state.filter;
  }

  @Selector()
  static fundId(state: ViewStateModel): number | null {
    return state.fundId;
  }

  @Selector()
  static sleeves(state: ViewStateModel): SleeveRef[] {
    return state.sleeves;
  }

  @Action(LoadFunds)
  loadFunds(ctx: StateContext<ViewStateModel>) {
    return this.api.funds().pipe(tap((funds) => ctx.patchState({ funds })));
  }

  @Action(SetInstrumentFilter)
  setFilter(ctx: StateContext<ViewStateModel>, action: SetInstrumentFilter) {
    ctx.patchState({ filter: action.filter });
  }

  @Action(SetFundSleeves)
  setFundSleeves(ctx: StateContext<ViewStateModel>, action: SetFundSleeves) {
    ctx.patchState({ fundId: action.fundId, sleeves: action.sleeves });
  }
}
